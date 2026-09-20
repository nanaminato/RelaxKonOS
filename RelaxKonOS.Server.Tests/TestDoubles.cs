sealed class CapturingPrivilegedTransport : IPrivilegedOperationTransport
{
    public PrivilegedOperationRequest? LastRequest { get; private set; }
    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new PrivilegedOperationResult(true));
    }
}

sealed class SystemAuthenticationTransport(PrivilegedOperationResult result) : IPrivilegedOperationTransport
{
    public PrivilegedOperationRequest? LastRequest { get; private set; }
    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(result);
    }
}

sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add(formatter(state, exception));
}

sealed class FixtureHttpClientFactory(byte[] payload) : IHttpClientFactory, IOutboundProxyHttpClientFactory
{
    public string? LastClientName { get; private set; }
    public string? LastUserAgent { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastClientName = name;
        return new(new FixtureHandler(payload, request => LastUserAgent = request.Headers.UserAgent.ToString()));
    }

    public Task<HttpClient> CreateAsync(OutboundProxyTarget target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(target.ToString());
        client.Timeout = timeout;
        return Task.FromResult(client);
    }

    private sealed class FixtureHandler(byte[] payload, Action<HttpRequestMessage> inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}

sealed class StaticProxySettingsService : IProxySettingsService
{
    public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProxySettingsDto(false, false, true, true, false, "warning", 7890));

    public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>Proxy settings whose mixed port is chosen by the test, so listener probing is deterministic.</summary>
sealed class TestProxySettingsService(int mixedPort) : IProxySettingsService
{
    public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProxySettingsDto(false, false, true, true, false, "warning", mixedPort));

    public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class StaticProxySecretStore : IProxyControllerSecretStore
{
    public Task<string> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult("controller-secret");
}

sealed class TestProxyPaths(string root) : IProxyPlatformPaths
{
    public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "engines", engineId, "versions");
    public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "engines", engineId, "data");
    public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
    public string GetStateDirectory() => Path.Combine(root, "state");
    public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
}

sealed class TestMihomoRuntimeProbe : IMihomoRuntimeProbe
{
    public Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken) => Task.FromResult(File.Exists(executablePath) ? "Mihomo v1.19.30" : null);
}

sealed class HealthyMihomoController : IMihomoControllerClient
{
    public Task<ControllerResult<bool>> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<bool>.Success(true));
    public Task<ControllerResult<IReadOnlyList<ProxyGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyGroupDto>>.Success([]));
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyRoutingModeDto(ProxyRoutingMode.Rule));
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) => Task.FromResult(new ProxyDelayDto(proxyName, 42, false));
    public Task<ControllerResult<IReadOnlyList<ProxyConnectionDto>>> GetConnectionsAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyConnectionDto>>.Success([]));
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyTrafficDto(0, 0, 0, 0, 0));
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ControllerResult<IReadOnlyList<ProxyLogEntryDto>>> GetLogsAsync(int limit, CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyLogEntryDto>>.Success([]));
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyDnsStatusDto(false, false, null));
    public Task<string?> ReloadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class StaticGroupMihomoController(IReadOnlyList<ProxyGroupDto> groups) : IMihomoControllerClient
{
    public Task<ControllerResult<bool>> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<bool>.Success(true));
    public Task<ControllerResult<IReadOnlyList<ProxyGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyGroupDto>>.Success(groups));
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyRoutingModeDto(ProxyRoutingMode.Rule));
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) => Task.FromResult(new ProxyDelayDto(proxyName, null, false));
    public Task<ControllerResult<IReadOnlyList<ProxyConnectionDto>>> GetConnectionsAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyConnectionDto>>.Success([]));
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyTrafficDto(0, 0, 0, 0, 0));
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ControllerResult<IReadOnlyList<ProxyLogEntryDto>>> GetLogsAsync(int limit, CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyLogEntryDto>>.Success([]));
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyDnsStatusDto(false, false, null));
    public Task<string?> ReloadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class DelayedHealthyMihomoController(int unavailableResponses) : IMihomoControllerClient
{
    private readonly HealthyMihomoController _healthy = new();
    private int _remainingUnavailableResponses = unavailableResponses;
    public int HealthChecks { get; private set; }

    public Task<ControllerResult<bool>> IsReachableAsync(CancellationToken cancellationToken)
    {
        HealthChecks++;
        return Task.FromResult(_remainingUnavailableResponses-- > 0
            ? ControllerResult<bool>.Failure(ProxyProblemCodes.ControllerUnavailable)
            : ControllerResult<bool>.Success(true));
    }
    public Task<ControllerResult<IReadOnlyList<ProxyGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken) => _healthy.GetGroupsAsync(cancellationToken);
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => _healthy.SelectGroupAsync(groupName, proxyName, cancellationToken);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => _healthy.GetRoutingModeAsync(cancellationToken);
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => _healthy.SetRoutingModeAsync(mode, cancellationToken);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) => _healthy.TestProxyDelayAsync(proxyName, url, timeoutMilliseconds, cancellationToken);
    public Task<ControllerResult<IReadOnlyList<ProxyConnectionDto>>> GetConnectionsAsync(CancellationToken cancellationToken) => _healthy.GetConnectionsAsync(cancellationToken);
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => _healthy.GetTrafficAsync(cancellationToken);
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => _healthy.CloseConnectionAsync(connectionId, cancellationToken);
    public Task<ControllerResult<IReadOnlyList<ProxyLogEntryDto>>> GetLogsAsync(int limit, CancellationToken cancellationToken) => _healthy.GetLogsAsync(limit, cancellationToken);
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => _healthy.GetDnsStatusAsync(cancellationToken);
    public Task<string?> ReloadAsync(CancellationToken cancellationToken) => _healthy.ReloadAsync(cancellationToken);
}

sealed class TestProxyPrivilegedOperations : IProxyPrivilegedOperations
{
    public bool FailReplacement { get; set; }
    public bool FailServiceInstallation { get; set; }
    public bool FailUninstalledServiceRemoval { get; set; }
    public bool InstalledService { get; private set; }
    public int RestartCount { get; private set; }
    private ProxyPrivilegedResult Result(bool replacement = false) => replacement && FailReplacement ? new(false, ProxyProblemCodes.PrivilegedOperationUnavailable) : new(true);
    public Task<ProxyPrivilegedResult> InstallRuntimeAsync(InstallProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> RemoveRuntimeAsync(RemoveProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> ReplaceRuntimeAsync(ReplaceProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Result(replacement: true));
    public Task<ProxyPrivilegedResult> InstallServiceAsync(InstallProxyServiceOperation request, CancellationToken cancellationToken)
    {
        if (FailServiceInstallation) return Task.FromResult(new ProxyPrivilegedResult(false, ProxyProblemCodes.PrivilegedOperationUnavailable));
        InstalledService = true; return Task.FromResult(Result());
    }
    public Task<ProxyPrivilegedResult> RemoveServiceAsync(RemoveProxyServiceOperation request, CancellationToken cancellationToken)
    {
        if (!InstalledService && FailUninstalledServiceRemoval) return Task.FromResult(new ProxyPrivilegedResult(false, ProxyProblemCodes.PrivilegedOperationUnavailable));
        InstalledService = false; return Task.FromResult(Result());
    }
    public Task<ProxyPrivilegedResult> SetServiceStartupAsync(SetProxyServiceStartupOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> StartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> StopServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> RestartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) { RestartCount++; return Task.FromResult(Result()); }
    public Task<ProxyPrivilegedResult> WriteProtectedConfigurationAsync(WriteProxyConfigurationOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> RestoreNetworkConfigurationAsync(RestoreProxyNetworkOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> RepairServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
}

sealed class RecordingGeoDataService : IProxyGeoDataService
{
    public int EnsureCalls { get; private set; }
    public Task<ProxyGeoDataDto> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyGeoDataDto(false));
    public Task<string?> EnsureBundledAsync(CancellationToken cancellationToken) { EnsureCalls++; return Task.FromResult<string?>(null); }
    public Task<string?> ConfigureFromServerFileAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
}

sealed class TransactionTestEngine : IProxyEngine
{
    public string EngineId => MihomoEngine.Id;
    public bool FailNextReload { get; set; }
    public string? LastValidatedConfiguration { get; private set; }
    public Task<ProxyEngineCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyEngineCapabilities(true, true, false, false, false, false));
    public Task<ProxyHealthDto> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyHealthDto(ProxyRuntimeState.Running, ProxyTunState.Disabled, ProxyHealthState.Healthy, true, true, true));
    public async Task<string?> ValidateConfigurationAsync(string configurationPath, CancellationToken cancellationToken)
    {
        LastValidatedConfiguration = await File.ReadAllTextAsync(configurationPath, cancellationToken);
        return null;
    }
    public Task<string?> ReloadAsync(CancellationToken cancellationToken)
    {
        var failed = FailNextReload; FailNextReload = false;
        return Task.FromResult<string?>(failed ? ProxyProblemCodes.ControllerUnavailable : null);
    }
    public Task<IReadOnlyList<ProxyGroupDto>> GetGroupsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProxyGroupDto>>([]);
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyRoutingModeDto(ProxyRoutingMode.Rule, ProxyProblemCodes.NotSupported));
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) => Task.FromResult(new ProxyDelayDto(proxyName, null, false, ProxyProblemCodes.NotSupported));
    public Task<IReadOnlyList<ProxyConnectionDto>> GetConnectionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProxyConnectionDto>>([]);
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyTrafficDto(0, 0, 0, 0, 0));
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
    public Task<IReadOnlyList<ProxyLogEntryDto>> GetLogsAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProxyLogEntryDto>>([]);
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyDnsStatusDto(false, false, null));
}

sealed class TestProxyNetworkSafetyPlatform : IProxyNetworkSafetyPlatform
{
    public bool SnapshotSafe { get; set; }
    public bool ApplySucceeds { get; set; } = true;
    public bool ManagementRouteVerifies { get; set; } = true;
    public int ApplyCount { get; private set; }
    public int RestoreCount { get; private set; }
    public Task<ProxyManagementRouteSnapshot?> CaptureManagementRouteAsync(CancellationToken cancellationToken) => Task.FromResult<ProxyManagementRouteSnapshot?>(new("test", DateTimeOffset.UtcNow, SnapshotSafe, "eth0", "192.0.2.1", ["loopback", "relaxkonos-listeners"]));
    public Task<bool> ApplyTunAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) { ApplyCount++; return Task.FromResult(ApplySucceeds); }
    public Task<bool> VerifyManagementRouteAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(ManagementRouteVerifies);
    public Task<bool> RestoreAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) { RestoreCount++; return Task.FromResult(true); }
}

sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
}

sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "RelaxKonOS.Server.Tests";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
}

sealed class SilentInstallationProgress : IInstallationProgress
{
    public Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class FakeWebServerProvider : IWebServerProvider
{
    public string ProviderId => "fake";
    public WebServerDto Instance { get; } = new("fake-instance", "fake", WebServerType.Nginx, WebServerManagementMode.Integrated,
        "/fake/nginx", null, "test", DateTimeOffset.UtcNow, new WebServerCapabilities(true, true, true));
    public WebServerIntegrationCandidateDto Candidate { get; } = new("fake-candidate", "fake", WebServerType.Nginx,
        "/fake/candidate-nginx", "/fake/nginx.conf", "test", DateTimeOffset.UtcNow);
    public string? IntegratedCandidateId { get; private set; }

    public Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebServerDto>>([Instance]);
    public Task<IReadOnlyList<WebServerIntegrationCandidateDto>> ListIntegrationCandidatesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebServerIntegrationCandidateDto>>([Candidate]);
    public Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<WebServerStatusDto?>(instanceId == Instance.Id ? new WebServerStatusDto(instanceId, WebServerRuntimeState.Running) : null);
    public Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<WebServerConfigTestResultDto?>(null);
    public Task<WebServerOperationDto?> IntegrateCandidateAsync(string candidateId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        if (candidateId != Candidate.Id) return Task.FromResult<WebServerOperationDto?>(null);
        IntegratedCandidateId = candidateId;
        return Task.FromResult<WebServerOperationDto?>(new(Guid.NewGuid(), candidateId, "integrate", WebServerOperationState.Succeeded,
            "completed", string.Empty, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
    public Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<WebServerOperationDto?> UninstallManagedAsync(string instanceId, string idempotencyKey, UninstallManagedWebServerRequest request, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebServerSiteDto>?>([]);
    public Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, CancellationToken cancellationToken) => Task.FromResult<WebServerSiteDto?>(null);
    public Task<bool?> DeleteSiteAsync(string instanceId, string siteId, CancellationToken cancellationToken) => Task.FromResult<bool?>(false);
}

sealed class FakePerformanceSource : ISystemPerformanceSource
{
    private int _sample;
    public int SampleCount => Volatile.Read(ref _sample);

    public ValueTask<RelaxKonOS.Protocol.SystemMonitor.PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new RelaxKonOS.Protocol.SystemMonitor.PerformanceInfoDto(
            new("test", 1, 1, null, null), new(1000, 0), [new("fs:test", "test", "/test")],
            [new("disk:test", "test", null, [])], [new("net:test", "test", null, [])], new(true, false, false, true, false, true, true, false)));

    public ValueTask<RawPerformanceSample> ReadAsync(CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _sample);
        return ValueTask.FromResult(new RawPerformanceSample(DateTimeOffset.UtcNow, index * System.Diagnostics.Stopwatch.Frequency,
            new RawCpuTimes(index * 100, index * 70, index * 20, index * 10, null, [new(index * 100, index * 70, index * 20, index * 10, null, [], null)], null),
            new RawMemory(1000, 600, null, null, 0, 0), [new("fs:test", 1000, 600)],
            [new("disk:test", index * 10, index * 5, index * 10, index * 5, index * 100, index * 100, index * 10, index * 5, 512)],
            [new("net:test", index * 500, index * 100, index * 5, index, 0, 0, 0, 0)], index));
    }
}

sealed class MemoryPermissionStore : IAppPermissionStore
{
    private readonly Dictionary<(AppId AppId, string Capability), IReadOnlyList<PermissionGrant>> _values = [];
    public IReadOnlyList<PermissionGrant> Get(AppId appId, string capability) =>
        _values.TryGetValue((appId, capability), out var values) ? values : Array.Empty<PermissionGrant>();
    public void Replace(AppId appId, string capability, IReadOnlyList<PermissionGrant> grants) => _values[(appId, capability)] = grants;
    public void Clear(AppId appId) { foreach (var key in _values.Keys.Where(key => key.AppId == appId).ToArray()) _values.Remove(key); }
}

sealed class TestPolicyProvider : IAppPolicyProvider
{
    public PermissionDecision GetDefaultDecision(AppIdentity identity, string capability, PermissionScope scope) =>
        identity.TrustLevel == AppTrustLevel.BuiltIn && capability == AppPermissions.ServerFilesRead && scope == PermissionScope.None
            ? PermissionDecision.Allow : PermissionDecision.Prompt;
}

/// <summary>
/// Pairs a proxy service with the recording configurator it was built around, so a test can prove
/// that the host was not touched without needing a real daemon-side mechanism.
/// </summary>
sealed class DockerProxyServiceFixture
{
    public DockerProxyServiceFixture(IDockerProxySettingsRepository settings, IDockerProxyResolver resolver,
        IDockerEngineService engine, bool supported, IDockerDesktopProxyReader? desktopProxy)
    {
        Configurator = new RecordingDockerEngineProxyConfigurator { IsSupported = supported };
        DesktopProxy = desktopProxy ?? new StaticDockerDesktopProxyReader(null);
        Service = new DockerProxyService(settings, resolver, Configurator, engine, DesktopProxy, NullLogger<DockerProxyService>.Instance);
    }

    public DockerProxyService Service { get; }
    public RecordingDockerEngineProxyConfigurator Configurator { get; }
    public IDockerDesktopProxyReader DesktopProxy { get; }
}

/// <summary>Stand-in for Docker Desktop's settings file, so a test controls what that app stores.</summary>
sealed class StaticDockerDesktopProxyReader(DockerDesktopProxyDto? snapshot) : IDockerDesktopProxyReader
{
    public DockerDesktopProxyDto? Snapshot { get; set; } = snapshot;

    public DockerDesktopProxyDto? Read() => Snapshot;
}

/// <summary>Stand-in for the host mechanism: records the lifecycle commands a caller asked for.</summary>
sealed class RecordingDockerEngineHostController : IDockerEngineHostController
{
    public List<DockerEngineAction> Requests { get; } = [];

    public string Platform => "test-mechanism";
    public bool IsSupported { get; set; } = true;
    public bool Succeed { get; set; } = true;

    public Task<DockerEngineHostCommandResult> ApplyAsync(DockerEngineAction action, CancellationToken cancellationToken = default)
    {
        Requests.Add(action);
        return Task.FromResult(Succeed
            ? new DockerEngineHostCommandResult(true, string.Empty)
            : new DockerEngineHostCommandResult(false, DockerEngineProblem.ActionFailed));
    }
}

/// <summary>Stand-in for the daemon-side writer: records which host changes were requested.</summary>
sealed class RecordingDockerEngineProxyConfigurator : IDockerEngineProxyConfigurator
{
    public List<bool> Requests { get; } = [];

    public string Platform => "test-mechanism";
    public bool IsSupported { get; set; } = true;
    public bool Succeed { get; set; } = true;

    public Task<DockerEngineProxyApplyResult> ApplyAsync(bool enabled, DockerProxyResolution resolution, CancellationToken cancellationToken = default)
    {
        Requests.Add(enabled);
        return Task.FromResult(Succeed
            ? new DockerEngineProxyApplyResult(true, string.Empty, DockerProxyDetail.RestartPending)
            : new DockerEngineProxyApplyResult(false, DockerProxyProblem.EngineApplyFailed));
    }
}

/// <summary>
/// Docker engine stub. Only <c>GetProxyStateAsync</c> and <c>GetStatusAsync</c> are exercised by the
/// proxy and engine-control services; every other member throws, so a future call through this stub
/// fails loudly rather than silently.
/// </summary>
class StubDockerEngine : DispatchProxy
{
    public DockerEngineProxyState? State { get; set; }
    public DockerStatusDto? Status { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IDockerEngineService.GetProxyStateAsync) => Task.FromResult(State),
        nameof(IDockerEngineService.GetStatusAsync) => Task.FromResult(Status ?? new DockerStatusDto(true, string.Empty, "29.8.0", "linux", "x64")),
        _ => throw new NotSupportedException(targetMethod?.Name),
    };
}
