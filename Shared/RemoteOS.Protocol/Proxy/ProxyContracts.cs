using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Proxy;

public enum ProxyOperatingMode { Tun, ListenerOnly, RemoteOSOnly }
public enum ProxyRuntimeMode { None, Managed, External }
public enum ProxyRuntimeState { NotInstalled, Installing, Stopped, Starting, Running, Reloading, Stopping, Updating, Recovering, Degraded, Failed }
public enum ProxyTunState { Disabled, Enabling, Enabled, Disabling, Recovering, Failed }
public enum ProxyHealthState { Unknown, Healthy, Degraded, Failed, RecoveryRequired }
public enum ProxyOperationState { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }
public enum ProxyLifecycleAction { Start, Stop, Restart }
public enum ProxySubscriptionDownloadRoute { Direct, SystemProxy }
public enum ProxyRoutingMode { Rule, Global, Direct }

/// <summary>All public Proxy failures use this lower-case dotted contract.</summary>
public static class ProxyProblemCodes
{
    public const string RuntimeNotInstalled = "proxy.runtime_not_installed";
    public const string RuntimeUnsupportedPlatform = "proxy.runtime_unsupported_platform";
    public const string RuntimeVersionUnsupported = "proxy.runtime_version_unsupported";
    public const string RuntimeArchiveUnavailable = "proxy.runtime_archive_unavailable";
    public const string RuntimeIntegrityFailed = "proxy.runtime_integrity_failed";
    public const string RuntimeHealthCheckFailed = "proxy.runtime_health_check_failed";
    public const string ExternalRuntimeInvalid = "proxy.external_runtime_invalid";
    public const string ServiceUnavailable = "proxy.service_unavailable";
    public const string PrivilegedOperationUnavailable = "proxy.privileged_operation_unavailable";
    public const string ConfigInvalid = "proxy.config_invalid";
    public const string ConfigApplyFailed = "proxy.config_apply_failed";
    public const string ControllerUnavailable = "proxy.controller_unavailable";
    public const string ControllerAuthenticationFailed = "proxy.controller_authentication_failed";
    public const string ControllerResponseInvalid = "proxy.controller_response_invalid";
    public const string ControllerTimeout = "proxy.controller_timeout";
    public const string ManagementRouteUnsafe = "proxy.management_route_unsafe";
    public const string PlatformCapabilityUnavailable = "proxy.platform_capability_unavailable";
    public const string TunPermissionRequired = "proxy.tun_permission_required";
    public const string TunActivationFailed = "proxy.tun_activation_failed";
    public const string RecoveryRequired = "proxy.recovery_required";
    public const string RecoveryFailed = "proxy.recovery_failed";
    public const string OperationInterrupted = "proxy.operation_interrupted";
    public const string IdempotencyKeyRequired = "proxy.idempotency_key_required";
    public const string PermissionDenied = "proxy.permission_denied";
    public const string NotSupported = "proxy.not_supported";
    public const string SubscriptionInvalid = "proxy.subscription_invalid";
    public const string SubscriptionFetchFailed = "proxy.subscription_fetch_failed";
    public const string SubscriptionSystemProxyUnavailable = "proxy.subscription_system_proxy_unavailable";
    public const string GeodataUnavailable = "proxy.geodata_unavailable";
    public const string GeodataInvalid = "proxy.geodata_invalid";
}

public static class ProxyApiRoutes
{
    public const string Proxy = $"/{RemoteOS.Protocol.Common.RemoteOsEndpoints.ApiVersionPrefix}/proxy";
    public const string Overview = Proxy;
    public const string Runtime = Proxy + "/runtime";
    public const string LifecyclePattern = "/lifecycle/{action}";
    public const string Tun = Proxy + "/tun";
    public const string Profiles = Proxy + "/profiles";
    public const string Subscriptions = Proxy + "/subscriptions";
    public const string SubscriptionPattern = "/subscriptions/{subscriptionId:guid}";
    public const string SubscriptionRefreshPattern = "/subscriptions/{subscriptionId:guid}/refresh";
    public const string SubscriptionContentPattern = "/subscriptions/{subscriptionId:guid}/content";
    public const string SubscriptionActivatePattern = "/subscriptions/{subscriptionId:guid}/activate";
    public const string ProfilePattern = "/profiles/{profileId:guid}";
    public const string ProfileConfigurationPattern = "/profiles/{profileId:guid}/configuration";
    public const string Groups = Proxy + "/groups";
    public const string GroupSelectionPattern = "/groups/{groupName}/selection";
    public const string Connections = Proxy + "/connections";
    public const string Traffic = Proxy + "/traffic";
    public const string ConnectionPattern = "/connections/{connectionId}";
    public const string Logs = Proxy + "/logs";
    public const string Dns = Proxy + "/dns";
    public const string Settings = Proxy + "/settings";
    public const string GeoData = Proxy + "/geodata";
    public const string Recovery = Proxy + "/recovery";
    public const string OperationsPattern = "/operations/{operationId:guid}";
    public const string RuntimeInstall = Runtime + "/install";
    public const string RuntimeDownload = Runtime + "/download";
    public const string RuntimeInstallFromFile = Runtime + "/install/from-file";
    public const string RuntimeRollback = Runtime + "/rollback";
    public const string RuntimeUninstall = Runtime + "/uninstall";
    public const string RuntimeExternalDetection = Runtime + "/detect-external";
    public const string Lifecycle = Proxy + "/lifecycle/{action}";
    public const string TunEnable = Tun + "/enable";
    public const string TunDisable = Tun + "/disable";
    public const string TunEmergencyDisable = Tun + "/emergency-disable";
    public const string ProfileActivatePattern = "/profiles/{profileId:guid}/activate";
    public const string ProfileConfigurationApplyPattern = "/profiles/{profileId:guid}/configuration/apply";
    public const string SubscriptionsRefresh = Subscriptions + "/refresh";
    public const string SubscriptionDownloadOptions = Subscriptions + "/download-options";
    public const string Routing = Proxy + "/routing";
    public const string GroupProxyDelayPattern = "/groups/{groupName}/proxies/{proxyName}/delay";
}

public sealed record ProxyEngineCapabilities(
    [property: JsonPropertyName("supportsConfigurationValidation")] bool SupportsConfigurationValidation,
    [property: JsonPropertyName("supportsReload")] bool SupportsReload,
    [property: JsonPropertyName("supportsGroups")] bool SupportsGroups,
    [property: JsonPropertyName("supportsConnections")] bool SupportsConnections,
    [property: JsonPropertyName("supportsBoundedLogs")] bool SupportsBoundedLogs,
    [property: JsonPropertyName("supportsDnsStatus")] bool SupportsDnsStatus);

public sealed record ProxyPlatformCapabilities(
    [property: JsonPropertyName("supportsTun")] bool SupportsTun,
    [property: JsonPropertyName("supportsAutoRoute")] bool SupportsAutoRoute,
    [property: JsonPropertyName("supportsAutoRedirect")] bool SupportsAutoRedirect,
    [property: JsonPropertyName("supportsDnsHijack")] bool SupportsDnsHijack,
    [property: JsonPropertyName("supportsNamedPipeController")] bool SupportsNamedPipeController,
    [property: JsonPropertyName("supportsUnixSocketController")] bool SupportsUnixSocketController,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

public sealed record ProxyHealthDto(
    [property: JsonPropertyName("runtimeState")] ProxyRuntimeState RuntimeState,
    [property: JsonPropertyName("tunState")] ProxyTunState TunState,
    [property: JsonPropertyName("state")] ProxyHealthState State,
    [property: JsonPropertyName("controllerReachable")] bool ControllerReachable,
    [property: JsonPropertyName("networkReachable")] bool NetworkReachable,
    [property: JsonPropertyName("managementRouteSafe")] bool ManagementRouteSafe,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

public sealed record ProxyRuntimeDto(
    [property: JsonPropertyName("engineId")] string EngineId,
    [property: JsonPropertyName("mode")] ProxyRuntimeMode Mode,
    [property: JsonPropertyName("state")] ProxyRuntimeState State,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("previousVersion")] string? PreviousVersion,
    [property: JsonPropertyName("integrityVerified")] bool IntegrityVerified,
    [property: JsonPropertyName("externalPathConfigured")] bool ExternalPathConfigured,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

public sealed record ProxyProfileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("engineId")] string EngineId,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>Safe subscription metadata. The source URL is encrypted server-side and is never returned.</summary>
public sealed record ProxySubscriptionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("profileId")] Guid ProfileId,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("lastUpdatedAt")] DateTimeOffset? LastUpdatedAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>Read-only source content requested by a privileged user. The source URL is never included.</summary>
public sealed record ProxySubscriptionContentDto(Guid SubscriptionId, string Content, DateTimeOffset RetrievedAt);
public sealed record ProxySubscriptionDownloadOptionsDto(bool SystemProxyAvailable);

public sealed record ProxyGroupDto(string Name, string Type, string? Selected, IReadOnlyList<string> Proxies);
public sealed record ProxyRoutingModeDto(ProxyRoutingMode Mode, string ProblemCode = "");
public sealed record ProxyDelayDto(string ProxyName, int? DelayMilliseconds, bool TimedOut, string ProblemCode = "");
public sealed record TestProxyDelayRequest(string Url, int TimeoutMilliseconds = 5000);
public sealed record ProxyConnectionDto(string Id, string Network, string Source, string Destination, string Rule, string Chains, DateTimeOffset StartedAt);
/// <summary>Bounded, controller-neutral traffic counters sampled from the active proxy engine.</summary>
public sealed record ProxyTrafficDto(long UploadBytesPerSecond, long DownloadBytesPerSecond, long UploadTotalBytes, long DownloadTotalBytes, long MemoryBytes, string ProblemCode = "");
public sealed record ProxyLogEntryDto(DateTimeOffset Timestamp, string Level, string Message);
public sealed record ProxyDnsStatusDto(bool Enabled, bool HijackEnabled, string? Mode, string ProblemCode = "");
/// <summary>Managed subset of Mihomo's top-level TUN configuration. Enabling TUN remains a separate protected operation.</summary>
public sealed record ProxyTunSettingsDto(
    string Stack,
    string DeviceName,
    bool AutoRoute,
    bool StrictRoute,
    bool AutoDetectInterface,
    string DnsHijack,
    int Mtu)
{
    public static ProxyTunSettingsDto Default { get; } = new("mixed", "Mihomo", true, false, true, "any:53", 1500);
}
/// <summary>Windows Internet Settings values owned by RemoteOS. PAC uses Windows auto-detection; it never accepts an untrusted PAC URL.</summary>
public sealed record ProxySystemProxyOptionsDto(
    bool UsePac = false,
    bool GuardEnabled = false,
    int GuardIntervalSeconds = 30,
    bool UseDefaultBypass = true,
    string BypassList = "")
{
    public static ProxySystemProxyOptionsDto Default { get; } = new();
}
public sealed record ProxySettingsDto(bool SystemProxyEnabled, bool AllowLan, bool DnsEnabled, bool Ipv6Enabled, bool UnifiedDelay,
    string LogLevel, int MixedPort, bool AllowInsecureSubscriptionSources = false, string SystemProxyHost = "127.0.0.1",
    ProxyTunSettingsDto? Tun = null, ProxySystemProxyOptionsDto? SystemProxy = null);
/// <summary>Metadata for the locally staged GeoIP database. The original Server path is never exposed.</summary>
public sealed record ProxyGeoDataDto(bool IsConfigured, long? SizeBytes = null);
public sealed record ProxyRecoveryStatusDto(bool RecoveryRequired, bool HasRecoveryMarker, DateTimeOffset? MarkerCreatedAt, string ProblemCode = "");
public sealed record ProxyOperationDto(Guid OperationId, string Kind, ProxyOperationState State, string Stage, string ProblemCode, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

public sealed record ProxyOverviewDto(string EngineId, ProxyEngineCapabilities EngineCapabilities, ProxyPlatformCapabilities PlatformCapabilities,
    ProxyRuntimeDto Runtime, ProxyHealthDto Health, ProxyOperatingMode OperatingMode, ProxyProfileDto? ActiveProfile,
    int ActiveConnections, ProxyRecoveryStatusDto Recovery, string? OperatingSystem = null);

public sealed record UpsertProxyProfileRequest(string Name, string EngineId, long? ExpectedRevision = null);
public sealed record ImportProxySubscriptionRequest(string Url, string? Name = null,
    ProxySubscriptionDownloadRoute DownloadRoute = ProxySubscriptionDownloadRoute.Direct);
public sealed record SelectProxyGroupRequest(string Proxy);
public sealed record ProxyTunRequest(Guid ProfileId);
public sealed record ProxyRuntimeRequest(string EngineId, string? Version = null, string? ExternalPath = null);
/// <summary>Trusted direct-download location for the selected managed runtime archive.</summary>
public sealed record ProxyRuntimeDownloadDto(string Version, string Url);
/// <summary>Installs a pinned Mihomo archive already present on the RemoteOS Server.</summary>
public sealed record InstallProxyRuntimeFromFileRequest(string EngineId, string? Version, string ArchivePath);
public sealed record ProxyLifecycleRequest(bool Confirmed = false);
public sealed record ApplyProxyConfigurationRequest(string Yaml);
public sealed record UpdateProxySettingsRequest(bool SystemProxyEnabled, bool AllowLan, bool DnsEnabled, bool Ipv6Enabled, bool UnifiedDelay,
    string LogLevel, int MixedPort, bool AllowInsecureSubscriptionSources = false, string SystemProxyHost = "127.0.0.1",
    ProxyTunSettingsDto? Tun = null, ProxySystemProxyOptionsDto? SystemProxy = null);
/// <summary>Selects a GeoIP database already accessible to the RemoteOS Server service account.</summary>
public sealed record ConfigureProxyGeoDataRequest(string FilePath);
public sealed record ProxyOperationAcceptedDto(Guid OperationId);
