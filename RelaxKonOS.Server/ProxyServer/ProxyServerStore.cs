using RelaxKonOS.ManagedRuntimes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.ProxyServer;

public sealed record StoredPrincipal(Guid Id, string DisplayName, string? RelaxKonOSUserId);

/// <summary>A principal's secret and every inbound it may use. The value never leaves the Server except as an export.</summary>
public sealed record StoredCredential(
    Guid Id, Guid PrincipalId, string Name, Guid[] InboundIds, string Value, bool Enabled, DateTimeOffset? ExpiresAt);

/// <summary>Only the hash of a subscription bearer token is persisted.</summary>
public sealed record StoredSubscription(
    Guid Id, Guid CredentialId, string TokenHash, string[] Formats, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, bool Revoked);

public sealed record ProxyServerReceipt(string Fingerprint, Guid OperationId, DateTimeOffset IssuedAt, Guid? ResourceId = null);

public sealed record ProxyServerState(
    ProxyServerConfiguration Configuration,
    StoredPrincipal[] Principals,
    StoredCredential[] Credentials,
    ProxyServerRealityKey[] Keys,
    ProxyServerMaterial? Published,
    ProxyServerMaterial? PreviousPublished,
    StoredSubscription[] Subscriptions,
    Dictionary<string, ProxyServerReceipt> Receipts)
{
    public ProxyServerMaterial? PendingPublication { get; init; }
    public bool PendingRollback { get; init; }
    public static ProxyServerState Empty => new(new(0, []), [], [], [], null, null, [], new(StringComparer.Ordinal));
}

internal readonly record struct ProxyServerExpirySchedule(bool PendingPublication, DateTimeOffset? DueAt)
{
    internal bool RequiresPass(DateTimeOffset now) => PendingPublication || DueAt <= now;
}

public sealed class ProxyServerStore(IProxyPlatformPaths paths, IDataProtectionProvider protection)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const int MaximumIndexBytes = 4096;
    private sealed record ExpiryIndex(string StateDigest, ProxyServerExpirySchedule Schedule);

    public SemaphoreSlim Gate { get; } = new(1);
    private readonly IDataProtector _protector = protection.CreateProtector("RelaxKonOS.ProxyServer.State.v1");
    private readonly IDataProtector _expiryProtector = protection.CreateProtector("RelaxKonOS.ProxyServer.ExpiryIndex.v1");
    private string PathName => Path.Combine(paths.GetStateDirectory(), "proxy-server.protected");
    private string ExpiryPath => PathName + ".expiry";

    public async Task<ProxyServerState> ReadAsync(CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(PathName);
        try { return Decode(await File.ReadAllTextAsync(PathName, ct)); }
        catch (FileNotFoundException) { return ProxyServerState.Empty; }
        catch (DirectoryNotFoundException) { return ProxyServerState.Empty; }
    }

    private ProxyServerState Decode(string protectedState) =>
        JsonSerializer.Deserialize<ProxyServerState>(_protector.Unprotect(protectedState), Json)
        ?? throw new InvalidDataException("proxy_server.state_invalid");

    internal async Task<ProxyServerExpirySchedule> ReadExpiryScheduleAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            RuntimeSourceStore.RequireNoLinks(PathName);
            FileStream stream;
            try { stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
            catch (FileNotFoundException) { return default; }
            catch (DirectoryNotFoundException) { return default; }
            await using (stream)
            {
                var index = await ReadIndexAsync(ct);
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (index?.StateDigest == digest) return index.Schedule;

                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var schedule = BuildExpirySchedule(Decode(await reader.ReadToEndAsync(ct)));
                await TryWriteIndexAsync(new(digest, schedule), ct);
                return schedule;
            }
        }
        finally { Gate.Release(); }
    }

    private async Task<ExpiryIndex?> ReadIndexAsync(CancellationToken ct)
    {
        try
        {
            RuntimeSourceStore.RequireNoLinks(ExpiryPath);
            await using var stream = new FileStream(ExpiryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumIndexBytes) return null;
            var bytes = new byte[MaximumIndexBytes + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
            if (count > MaximumIndexBytes) return null;
            return JsonSerializer.Deserialize<ExpiryIndex>(_expiryProtector.Unprotect(Encoding.UTF8.GetString(bytes, 0, count)), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or RuntimePolicyException)
        {
            // The sidecar is an optimization. Only the original protected ledger can establish responsibility.
            return null;
        }
    }

    private static ProxyServerExpirySchedule BuildExpirySchedule(ProxyServerState state)
    {
        if (state.PendingPublication is not null) return new(true, null);
        DateTimeOffset? earliest = null;
        if (state.Published is not { } published) return default;
        foreach (var grant in published.Credentials)
        {
            var credential = FindEnabledCredential(state, grant);
            if (credential is null) return new(false, DateTimeOffset.MinValue);
            if (credential.ExpiresAt is { } expiry && (earliest is null || expiry < earliest))
                earliest = expiry;
        }
        return new(false, earliest);
    }

    internal static StoredCredential? FindEnabledCredential(ProxyServerState state, ProxyServerSecret grant) =>
        state.Credentials.FirstOrDefault(credential => credential.Id == grant.Id && credential.Enabled && credential.Value == grant.Value);

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public async Task WriteAsync(ProxyServerState state, CancellationToken ct)
    {
        var protectedState = Encoding.UTF8.GetBytes(_protector.Protect(JsonSerializer.Serialize(state, Json)));
        RuntimeSourceStore.RequireNoLinks(PathName);
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        // Publish the derived index first. An interrupted commit can leave a mismatched index,
        // which never overrides the digest of the authoritative file.
        await TryWriteIndexAsync(new(Digest(protectedState), BuildExpirySchedule(state)), ct);
        await WriteAtomicAsync(PathName, protectedState, ct);
    }

    private async Task TryWriteIndexAsync(ExpiryIndex index, CancellationToken ct)
    {
        try
        {
            RuntimeSourceStore.RequireNoLinks(ExpiryPath);
            RuntimeSourceStore.RequireNoLinks(ExpiryPath + ".new");
            var protectedIndex = Encoding.UTF8.GetBytes(_expiryProtector.Protect(JsonSerializer.Serialize(index, Json)));
            await WriteAtomicAsync(ExpiryPath, protectedIndex, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or RuntimePolicyException) { }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + ".new";
        RuntimeSourceStore.RequireNoLinks(temporary);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            {
                await file.WriteAsync(bytes, ct);
                file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
