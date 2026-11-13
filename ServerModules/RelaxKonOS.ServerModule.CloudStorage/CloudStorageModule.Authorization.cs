using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

public sealed partial class CloudStorageModule
{
    private const string AuthorizationStore = "authorization.bin";
    private readonly SemaphoreSlim _authorizationGate = new(1, 1);
    private Dictionary<string, AuthorizationConfiguration> _authorizationConfigurations = [];
    private readonly Dictionary<Guid, AuthorizationSession> _authorizationSessions = [];
    private sealed record AuthorizationConfiguration(string ClientId, string Secret, Guid Revision);
    private sealed class AuthorizationSession
    {
        internal required Guid Id, Owner, Revision;
        internal required string Protocol, Name;
        internal required bool Writes;
        internal required DateTimeOffset Expires;
        internal string Status = "pending";
        internal string? Url, UserCode, DeviceCode, QrCodeUrl;
        internal int Interval = 5;
        internal DateTimeOffset NextPoll;
        internal Guid? ConnectionId, ReplaceConnectionId, ReplaceRevision;
        internal CloudAuthorizationSessionDto Describe() => new(Id, Status, Url, UserCode, Expires, Interval, ConnectionId, QrCodeUrl);
    }

    private async Task LoadAuthorizationAsync(CancellationToken ct)
    {
        var bytes = await _context.Storage.ReadAsync(AuthorizationStore, MaxStoreBytes, ct);
        if (bytes is null) return;
        if (bytes.Length <= EnvelopeSize || bytes[0] != 1) throw new InvalidDataException(AuthorizationStore);
        var clear = new byte[bytes.Length - EnvelopeSize];
        try {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(bytes.AsSpan(1, NonceSize), bytes.AsSpan(EnvelopeSize), bytes.AsSpan(1 + NonceSize, TagSize), clear);
            _authorizationConfigurations = JsonSerializer.Deserialize<Dictionary<string, AuthorizationConfiguration>>(clear)
                ?? throw new InvalidDataException(AuthorizationStore);
            foreach (var protocol in _authorizationConfigurations.Keys.Where(protocol => !OAuthDriveConnector.Supports(protocol)).ToArray())
                _authorizationConfigurations.Remove(protocol);
        } finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private async Task SaveAuthorizationAsync(Dictionary<string, AuthorizationConfiguration> configs, CancellationToken ct)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(configs);
        try {
            var bytes = new byte[EnvelopeSize + clear.Length]; bytes[0] = 1;
            RandomNumberGenerator.Fill(bytes.AsSpan(1, NonceSize));
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(bytes.AsSpan(1, NonceSize), clear, bytes.AsSpan(EnvelopeSize), bytes.AsSpan(1 + NonceSize, TagSize));
            await _context.Storage.WriteAsync(AuthorizationStore, bytes, ct);
            _authorizationConfigurations = configs;
        } finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private void MapAuthorizationEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/authorization/providers", (Delegate)((HttpContext http) => WithUser(http, async user => {
            await _authorizationGate.WaitAsync(http.RequestAborted);
            try { return Results.Ok(new[] { new CloudAuthorizationProviderDto("baidu-netdisk", _authorizationConfigurations.ContainsKey("baidu-netdisk")) }); }
            finally { _authorizationGate.Release(); }
        })));
        group.MapPut("/authorization/configuration/{protocol}", (string protocol, CloudAuthorizationConfigurationRequest request, HttpContext http) => WithUser(http, async user => {
            if (!IsController(http)) return Results.StatusCode(403);
            if (!OAuthDriveConnector.Supports(protocol) || !ValidValue(request.ClientId, 512) || !ValidValue(request.ClientSecret, 2048)) return Results.BadRequest();
            await _authorizationGate.WaitAsync(http.RequestAborted);
            try {
                var configs = new Dictionary<string, AuthorizationConfiguration>(_authorizationConfigurations) {
                    [protocol] = new(request.ClientId, request.ClientSecret, Guid.NewGuid())
                };
                await SaveAuthorizationAsync(configs, http.RequestAborted);
                foreach (var session in _authorizationSessions.Values.Where(s => s.Protocol == protocol && s.Status == "pending")) { session.Status = "expired"; ClearAuthorizationSecrets(session); }
                return Results.NoContent();
            } finally { _authorizationGate.Release(); }
        }));
        group.MapPost("/authorization/sessions", (CloudAuthorizationStartRequest request, HttpContext http) => WithUser(http, async user => {
            if (!OAuthDriveConnector.Supports(request.Protocol) || !ValidValue(request.Name, 100)) return Results.BadRequest();
            await _authorizationGate.WaitAsync(http.RequestAborted);
            try {
                SweepAuthorization();
                if (!_authorizationConfigurations.TryGetValue(request.Protocol, out var config)) return Results.Conflict();
                if (_authorizationSessions.Count >= 256 || _authorizationSessions.Values.Count(s => s.Owner == user && s.Status == "pending") >= 4) return Results.StatusCode(429);
                var replacement = request.ConnectionId is { } id
                    ? Volatile.Read(ref _connections).FirstOrDefault(item =>
                        item.Id == id && item.Owner == user && item.Settings.Protocol == request.Protocol)
                    : null;
                if (request.ConnectionId is not null && replacement is null) return Results.NotFound();
                var session = new AuthorizationSession { Id = Guid.NewGuid(), Owner = user, Revision = config.Revision,
                    Protocol = request.Protocol, Name = request.Name.Trim(), Writes = request.EnableWrites,
                    ReplaceConnectionId = replacement?.Id, ReplaceRevision = replacement?.Revision,
                    Expires = DateTimeOffset.UtcNow.AddMinutes(10) };
                await StartBaiduAuthorizationAsync(session, config, http.RequestAborted);
                _authorizationSessions.Add(session.Id, session);
                return Results.Ok(session.Describe());
            } finally { _authorizationGate.Release(); }
        }));
        group.MapGet("/authorization/sessions/{id:guid}", (Guid id, HttpContext http) => WithUser(http, async user => {
            await _authorizationGate.WaitAsync(http.RequestAborted);
            try {
                SweepAuthorization();
                if (!_authorizationSessions.TryGetValue(id, out var s) || s.Owner != user) return Results.NotFound();
                if (s.Status == "pending" && DateTimeOffset.UtcNow >= s.NextPoll)
                    await PollBaiduAuthorizationAsync(s, http.RequestAborted);
                return Results.Ok(s.Describe());
            } finally { _authorizationGate.Release(); }
        }));
        group.MapDelete("/authorization/sessions/{id:guid}", (Guid id, HttpContext http) => WithUser(http, async user => {
            await _authorizationGate.WaitAsync(http.RequestAborted);
            try {
                if (!_authorizationSessions.TryGetValue(id, out var s) || s.Owner != user) return Results.NotFound();
                if (s.Status == "pending") { s.Status = "cancelled"; ClearAuthorizationSecrets(s); }
                return Results.NoContent();
            } finally { _authorizationGate.Release(); }
        }));
    }

    private static bool ValidValue(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static void ClearAuthorizationSecrets(AuthorizationSession s) { s.DeviceCode = null; s.Url = null; s.UserCode = null; s.QrCodeUrl = null; }
    private void SweepAuthorization()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var s in _authorizationSessions.Values) if (s.Status == "pending" && s.Expires <= now) { s.Status = "expired"; ClearAuthorizationSecrets(s); }
        foreach (var id in _authorizationSessions.Values.Where(s => s.Expires.AddMinutes(5) <= now).Select(s => s.Id).ToArray()) _authorizationSessions.Remove(id);
    }
    private async Task PersistAuthorizationAsync(AuthorizationSession s, AuthorizationConfiguration config, string refresh, CancellationToken ct)
    {
        var settings = Validate(new CloudConnectionRequest(s.Name, s.Protocol, "", null, null, null,
            config.ClientId, config.Secret, refresh, EnableWrites: s.Writes), false);
        var connection = new StoredConnection(s.ReplaceConnectionId ?? Guid.NewGuid(), s.Owner, settings, Guid.NewGuid());
        await ChangeAsync(_context, items =>
        {
            if (s.ReplaceConnectionId is null)
                return items.Length >= 128 ? throw new ArgumentException("connection limit") : [.. items, connection];
            if (!items.Any(item => item.Id == connection.Id && item.Owner == s.Owner && item.Revision == s.ReplaceRevision))
                throw new InvalidOperationException("cloud-storage-connection-changed");
            return items.Select(item => item.Id == connection.Id ? connection : item).ToArray();
        }, ct);
        s.ConnectionId = connection.Id; s.Status = "completed";
    }
}
