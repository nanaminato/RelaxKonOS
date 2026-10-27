using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>Server-owned configuration uses the server management route, never a fabricated user.</summary>
public sealed class ServerRuntimeRegistryClient(HttpClient http, IAuthSession session, IHostElevationBroker grants, Guid instanceId) : IRegistryClient
{
    private readonly string? _origin = session.EffectiveBaseUrl;
    private readonly Guid? _actor = session.CurrentUser?.Id;
    public IRegistryClient ForUser(Guid user) => throw new InvalidOperationException("Server runtime settings have no user owner.");
    public IRegistryClient ForServer(Guid expectedInstance) => expectedInstance == instanceId ? this : throw new InvalidOperationException("Server instance changed.");
    public Task<RegistryContextDto> GetContextAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<RegistryUserDto>> ListUsersAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public async Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken ct = default)
    {
        if (scope != RegistryScope.Server || path != "Server\\Runtime") throw new InvalidOperationException("Invalid server runtime target.");
        return Fields(await SendAsync(HttpMethod.Get, null, ct));
    }
    public Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string path, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RegistryKeyDto>>([]);
    public async Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken ct = default)
    {
        if (request.Scope != RegistryScope.Server || request.Path != "Server\\Runtime" || request.ExpectedRevision is null) throw new InvalidOperationException("Invalid server runtime target.");
        var saved = await SendAsync(HttpMethod.Put, new(instanceId, request.Name, request.Value, request.ExpectedRevision.Value), ct);
        return Fields(saved).Single(field => field.Name == request.Name);
    }
    private Task<ServerRuntimeSnapshot> SendAsync(HttpMethod method, PutServerRuntimeFieldRequest? body, CancellationToken ct) =>
        grants.ExecuteAsync(HostElevationCapability.ServerRuntimeConfigure, ServerRuntimeRoutes.ElevationTarget, async () =>
        {
            if (session.State != AuthSessionState.Authenticated || session.EffectiveBaseUrl != _origin || session.CurrentUser?.Id != _actor || session.Tokens is null)
                throw new InvalidOperationException("The server management session changed.");
            using var request = new HttpRequestMessage(method, new Uri(new Uri(_origin!), ServerRuntimeRoutes.Settings.TrimStart('/')));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
            if (body is not null) request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new RelaxKonOSAuthException(await response.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, ct)
                    ?? new("about:blank", "Server settings", (int)response.StatusCode, null, null));
            return await response.Content.ReadFromJsonAsync<ServerRuntimeSnapshot>(RelaxKonOSJsonOptions.Default, ct)
                ?? throw new InvalidOperationException("Missing server runtime settings.");
        }, ct);
    private IReadOnlyList<RegistryEntryDto> Fields(ServerRuntimeSnapshot snapshot)
    {
        if (snapshot.InstanceId != instanceId) throw new InvalidOperationException("The server instance changed. Reopen the editor.");
        var defaults = JsonSerializer.SerializeToElement(ServerRuntimeSettings.Default, RelaxKonOSJsonOptions.Default);
        return JsonSerializer.SerializeToElement(snapshot.Settings, RelaxKonOSJsonOptions.Default).EnumerateObject().Select(property =>
        {
            var bounds = property.Name switch { "registryFlushDelayMilliseconds" => (100, 30000), "notificationCoalesceMilliseconds" => (0, 1000), _ => (1, 300) };
            var boolean = property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            return new RegistryEntryDto(RegistryScope.Server, "Server\\Runtime", property.Name, boolean ? RegistryValueType.Boolean : RegistryValueType.Number, property.Value.Clone(), snapshot.Revision,
                RegistryEntryState.Synced, snapshot.UpdatedAt, snapshot.UpdatedAt, RegistryApplyMode.Immediate, null, null, null)
            {
                Address = new(new Uri(_origin!).GetLeftPart(UriPartial.Authority), null, RegistryScope.Server, snapshot.InstanceId, "Server\\Runtime"),
                Schema = new(property.Name, true, false, false, defaults.GetProperty(property.Name).Clone())
                { Editor = boolean ? RegistryEditorKind.Boolean : RegistryEditorKind.Integer, Minimum = boolean ? null : bounds.Item1, Maximum = boolean ? null : bounds.Item2 },
            };
        }).ToArray();
    }
    public Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RegistryEntryDto> RenameAsync(RenameRegistryEntryRequest request, CancellationToken ct = default) => throw new NotSupportedException();
}
