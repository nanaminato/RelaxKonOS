using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Client.Apps.ProxyServer;

/// <summary>
/// Closed route set for the managed runtime page. The view model can select a structured source and
/// release, but never supplies a URL, executable path, shell command, or native installation argument.
/// </summary>
public interface IRuntimeCatalogClient
{
    Task<IReadOnlyList<RuntimeSourceView>> SourcesAsync(CancellationToken ct);
    Task<RuntimeCatalogSnapshot> CatalogAsync(string sourceId, bool includePrerelease, CancellationToken ct);
    Task<RuntimeCatalogSnapshot> RefreshAsync(string sourceId, bool includePrerelease, CancellationToken ct);
    Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct);
    Task<RuntimePlanResult> PlanAsync(string? sourceId, RuntimePlanRequest request, CancellationToken ct);
    Task<InstallationOperationDto?> StartAsync(InstallationOperationKind kind, ManagedRuntimeInstallationRequest request,
        string key, CancellationToken ct);
    Task<InstallationOperationDto?> OperationAsync(Guid id, CancellationToken ct);
    /// <summary>Records a cancellation request; only the operation's own result says whether it was early enough.</summary>
    Task<InstallationOperationDto?> CancelAsync(Guid id, string key, CancellationToken ct);
    Task<RuntimeRecoverySnapshot> RecoveryAsync(CancellationToken ct);
    Task<RuntimeSwitchResult> RetryRecoveryAsync(Guid operationId, CancellationToken ct);
    Task<RuntimeRecoverySnapshot> StopRecoveryAsync(Guid operationId, CancellationToken ct);
    Task<RuntimeSwitchResult> CloseRecoveryAsync(Guid operationId, CancellationToken ct);
    Task<bool> ElevateAsync(string password, CancellationToken ct);
}

public sealed class RemoteRuntimeCatalogClient(HttpClient http, IAuthSession session) : IRuntimeCatalogClient
{
    private readonly object? _login = session.CurrentSession;

    public Task<IReadOnlyList<RuntimeSourceView>> SourcesAsync(CancellationToken ct) =>
        SendAsync<IReadOnlyList<RuntimeSourceView>>(HttpMethod.Get, RuntimeRoutes.Sources, null, null, ct);

    public Task<RuntimeCatalogSnapshot> CatalogAsync(string sourceId, bool includePrerelease, CancellationToken ct) =>
        SendAsync<RuntimeCatalogSnapshot>(HttpMethod.Get, CatalogRoute(sourceId, includePrerelease), null, null, ct);

    public Task<RuntimeCatalogSnapshot> RefreshAsync(string sourceId, bool includePrerelease, CancellationToken ct) =>
        SendAsync<RuntimeCatalogSnapshot>(HttpMethod.Post, RefreshRoute(sourceId, includePrerelease), new { },
            Guid.NewGuid().ToString("N"), ct);

    public Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct) =>
        SendAsync<RuntimeInstallationSnapshot>(HttpMethod.Get, RuntimeRoutes.SingBoxInstallation, null, null, ct);

    public Task<RuntimePlanResult> PlanAsync(string? sourceId, RuntimePlanRequest request, CancellationToken ct) =>
        SendAsync<RuntimePlanResult>(HttpMethod.Post, RuntimeRoutes.SingBoxPlan, new RuntimePlanSelection(sourceId, request),
            Guid.NewGuid().ToString("N"), ct);

    public Task<InstallationOperationDto?> StartAsync(InstallationOperationKind kind, ManagedRuntimeInstallationRequest request,
        string key, CancellationToken ct) =>
        SendAsync<InstallationOperationDto?>(HttpMethod.Post, InstallationApiRoutes.Start(InstallationServiceId.SingBox, kind),
            request, key, ct);

    public Task<InstallationOperationDto?> OperationAsync(Guid id, CancellationToken ct) =>
        SendAsync<InstallationOperationDto?>(HttpMethod.Get, InstallationApiRoutes.Operation(id), null, null, ct);

    public Task<InstallationOperationDto?> CancelAsync(Guid id, string key, CancellationToken ct) =>
        SendAsync<InstallationOperationDto?>(HttpMethod.Post, InstallationApiRoutes.Cancel(id), new { }, key, ct);

    public Task<RuntimeRecoverySnapshot> RecoveryAsync(CancellationToken ct) =>
        SendAsync<RuntimeRecoverySnapshot>(HttpMethod.Get, RuntimeRoutes.SingBoxRecovery, null, null, ct);

    public Task<RuntimeSwitchResult> RetryRecoveryAsync(Guid operationId, CancellationToken ct) =>
        SendAsync<RuntimeSwitchResult>(HttpMethod.Post, RuntimeRoutes.SingBoxRecoveryRetry,
            new RuntimeRecoveryActionRequest(operationId), Guid.NewGuid().ToString("N"), ct);

    public Task<RuntimeRecoverySnapshot> StopRecoveryAsync(Guid operationId, CancellationToken ct) =>
        SendAsync<RuntimeRecoverySnapshot>(HttpMethod.Post, RuntimeRoutes.SingBoxRecoveryStop,
            new RuntimeRecoveryActionRequest(operationId), Guid.NewGuid().ToString("N"), ct);

    public Task<RuntimeSwitchResult> CloseRecoveryAsync(Guid operationId, CancellationToken ct) =>
        SendAsync<RuntimeSwitchResult>(HttpMethod.Post, RuntimeRoutes.SingBoxRecoveryClose,
            new RuntimeRecoveryActionRequest(operationId, Confirmed: true), Guid.NewGuid().ToString("N"), ct);

    /// <summary>The same elevation grant the installation ledger requires, requested in place of a silent failure.</summary>
    public async Task<bool> ElevateAsync(string password, CancellationToken ct) =>
        (await SendAsync<HostElevationResult>(HttpMethod.Post, PrivilegedApiRoutes.Elevation,
            new HostElevationRequest(HostElevationCapability.SingBoxInstall, "singbox", password), null, ct)).Elevated;

    private static string CatalogRoute(string sourceId, bool includePrerelease) =>
        RuntimeRoutes.Catalog.Replace("{sourceId}", Segment(sourceId), StringComparison.Ordinal)
        + (includePrerelease ? "?includePrerelease=true" : "");

    private static string RefreshRoute(string sourceId, bool includePrerelease) =>
        RuntimeRoutes.Refresh.Replace("{sourceId}", Segment(sourceId), StringComparison.Ordinal)
        + (includePrerelease ? "?includePrerelease=true" : "");

    private static string Segment(string sourceId)
    {
        // Mirrors the Server's own source-identifier rule so a stale or hostile id never becomes a route.
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128
            || sourceId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException(nameof(sourceId));
        return Uri.EscapeDataString(sourceId);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, string? key, CancellationToken ct)
    {
        Verify();
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct);
        Verify();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.EffectiveBaseUrl!), route.TrimStart('/')))
        { Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await http.SendAsync(request, ct);
        Verify();
        if (!response.IsSuccessStatusCode)
        {
            var problem = "runtime.request_failed";
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("code", out var code) && !string.IsNullOrWhiteSpace(code.GetString()))
                    problem = code.GetString()!;
                else if (json.RootElement.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()))
                    problem = title.GetString()!;
            }
            catch (JsonException) { }
            throw new HttpRequestException(problem, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new InvalidDataException("runtime.response_invalid");
    }

    private void Verify()
    {
        if (_login is null || !ReferenceEquals(_login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
            throw new InvalidOperationException("proxy_server.session_changed");
    }
}
