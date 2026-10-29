using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Protocol.Privileged;

/// <summary>Versioning and size limits for the local Helper protocol.</summary>
public static class PrivilegedOperationProtocol
{
    public const string Version = "1.1";
    public const int MaximumRequestBytes = 16 * 1024 * 1024;
    public const int MaximumFileContentBytes = 12 * 1024 * 1024;
}

/// <summary>
/// Closed set of operations understood by the local Helper. Do not add a command, executable,
/// shell, argument list, working directory, or privileged process environment fields.
/// Dedicated structured host configuration operations are permitted; execution environment injection is not.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrivilegedOperationKind>))]
public enum PrivilegedOperationKind
{
    RuntimeSources,
    RuntimeCatalogGet,
    RuntimeCatalogRefresh,
    SingBoxRuntimeQuery,
    SingBoxRuntimePlan,
    SingBoxRuntimeExecute,
    SingBoxRuntimeRecover,
    /// <summary>Records a cancellation request for one running transaction. It only writes the request
    /// marker, so it never blocks behind the instance lock the transaction itself holds; the running
    /// transaction decides whether it is still before the switch checkpoint.</summary>
    SingBoxRuntimeCancel,
    /// <summary>Reads the unfinished transaction and the fixed host's live service observation.</summary>
    SingBoxRuntimeRecoveryQuery,
    /// <summary>Stops the managed service and disables its start-on-boot registration. The ledger is not touched.</summary>
    SingBoxRuntimeRecoveryStop,
    /// <summary>Closes an unrecoverable transaction after the host proves nothing is running.</summary>
    SingBoxRuntimeRecoveryClose,
    MihomoRuntimeQuery,
    MihomoRuntimePlan,
    MihomoRuntimeExecute,
    MihomoRuntimeRecover,
    MihomoRuntimeCancel,
    MihomoRuntimeRecoveryQuery,
    MihomoRuntimeRecoveryStop,
    MihomoRuntimeRecoveryClose,
    MihomoRuntimeConfigure,
    /// <summary>Places the declared GEO resource set from the installed package into the managed data directory.</summary>
    MihomoRuntimeResources,
    MihomoRuntimeResourceQuery,
    ProxyServerStatus,
    ProxyServerAction,
    ProxyServerValidate,
    ProxyServerApply,
    ProxyServerGenerateKeys,
    ManagedRuntime,
    FileRead,
    FileListDirectory,
    FileWrite,
    FileDelete,
    FileRename,
    FileMove,
    FileCopy,
    FileUpload,
    /// <summary>Appends one chunk of an upload session's staging file at an explicit offset. The Helper
    /// re-validates the staging name shape, so this can never address an arbitrary file.</summary>
    FileUploadChunk,
    /// <summary>Publishes a staging file as its destination inside the same directory. The destination
    /// is named by a single file-name component, never by a full path.</summary>
    FileUploadCommit,
    FileCreateDirectory,
    FileGetSpecialLocations,
    FileGetInfo,
    FileGetProperties,
    FileSetUnixPermissions,
    FileSetUnixOwnership,
    FileGetStagingLength,
    FileDeleteStaging,
    FileCreateStaging,
    NativeServiceAction,
    NginxSystemServiceAction,
    NginxPackageInstall,
    /// <summary>
    /// Installs the exact Debian package identity a caller froze. The Helper re-resolves the package
    /// itself and refuses when the version, repository or digest moved, so a stale freeze can never
    /// install something else. A package transaction has no supported rollback.
    /// </summary>
    NginxPackageUninstall,
    /// <summary>
    /// Read-only resolution of the Nginx package the host's own package manager would install.
    /// It reads signed repository metadata and never downloads, installs, or changes a source.
    /// </summary>
    ComponentPackageQuery,
    ComponentPackageInstall,
    ComponentPackageChange,
    ComponentPackageRecover,
    NginxArchiveQuery,
    NginxArchiveInstall,
    FrpArchiveCatalog,
    NginxConfigurationTest,
    NginxRuntimeStatus,
    NginxWriteManagedFile,
    NginxMoveManagedFile,
    NginxDeleteManagedFile,
    NginxGrantStaticSiteReadAccess,
    ProxyMihomoServiceAction,
    ProxyMihomoInstallSystemService,
    ProxyMihomoRemoveSystemService,
    LinuxSystemProxyRead,
    LinuxSystemProxyApply,
    GitPackageInstall,
    DockerEngineInstall,
    HostTimeRead,
    TailscaleStatus,
    TailscaleControlRead,
    TailscaleControlExecute,
    TailscaleControlReceipt,
    TailscaleRecoveryRead,
    TailscaleRecoveryClose,
    TailscaleRecoveryReceipt,
    /// <summary>Publishes the confirmed first-login intent before the fixed native login command.</summary>
    TailscaleLoginBegin,
    /// <summary>Reads the URL-free login baseline so Server can prepare a confirmed plan.</summary>
    TailscaleLoginObserve,
    /// <summary>Reads the original login receipt. Missing evidence stays unknown, never success.</summary>
    TailscaleLoginReceipt,
    /// <summary>Re-reads the original challenge through the dedicated short-lived delivery path.</summary>
    TailscaleLoginChallenge,
    /// <summary>Reads a stable login observation for manual recovery preview.</summary>
    TailscaleLoginRecoveryRead,
    /// <summary>Closes one unresolved login attempt after explicit confirmation and revision agreement.</summary>
    TailscaleLoginClose,
    /// <summary>Reads the original login closure evidence.</summary>
    TailscaleLoginRecoveryReceipt,
    HostNetworkRead,
    HostNetworkApply,
    HostNetworkConfirm,
    HostTimeApply,
    HostIdentityRead,
    HostIdentityApply,
    HostEnvironmentRead,
    HostEnvironmentApply,
    FirewallWindowsStatus,
    FirewallWindowsRules,
    FirewallWindowsSetEnabled,
    FirewallWindowsSetDefaults,
    FirewallWindowsCreateRule,
    FirewallWindowsReplaceRule,
    FirewallWindowsDeleteRule,
    FirewallUfwStatus,
    FirewallUfwSetEnabled,
    FirewallUfwSetDefaults,
    FirewallUfwCreateRule,
    FirewallUfwReplaceRule,
    FirewallUfwDeleteRule,
    SmbDetect,
    SmbPackageInstall,
    SmbServiceAction,
    SmbReadManagedConfiguration,
    SmbReadUsers,
    SmbApplyManagedConfiguration,
    SmbApplyWindowsShare,
    SmbRemoveWindowsShare,
    SmbSetWindowsServerSecurity,
    SmbSetUserEnabled,
    SmbSetUserPassword,
    AuthenticateSystemUser,
    /// <summary>Checks the current host sudo policy for a canonical Linux account. This is a
    /// classification query, never an authorization grant.</summary>
    CheckHostAdministrator,
    /// <summary>Writes the fixed Linux docker.service proxy drop-in and restarts the daemon.</summary>
    DockerEngineConfigureProxy,
    /// <summary>Starts, stops, or restarts the fixed Linux <c>docker.service</c> unit.</summary>
    DockerEngineServiceAction,
    /// <summary>Returns bounded raw process measurements from the privileged host context.</summary>
    ProcessSnapshot,
    /// <summary>Terminates one host process instance after validating its PID and creation time.</summary>
    ProcessTerminate,
    /// <summary>Returns a PNG or SVG icon for one verified host process instance.</summary>
    ProcessIcon,
    SquidStatus,
    SquidApplyConfiguration,
    SquidServiceAction,
    SquidValidateConfiguration,
    SquidAccountsRead,
    SquidAccountMutate,
}

/// <summary>
/// Non-secret outcome of the fixed Linux system-account authentication operation. This is
/// deliberately separate from transport success: callers can distinguish a rejected password
/// from an unavailable Helper without receiving PAM diagnostics or account secrets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SystemAuthenticationResult>))]
public enum SystemAuthenticationResult
{
    Success,
    InvalidCredentials,
    AccountLocked,
    PasswordExpired,
    AccountUnavailable,
    PermissionDenied,
    PamError,
    InternalError,
}

[JsonConverter(typeof(JsonStringEnumConverter<PrivilegedFileAuthorizationSource>))]
public enum PrivilegedFileAuthorizationSource { ManualGrant, HostAdministrator, HostRoot }

[JsonConverter(typeof(JsonStringEnumConverter<PrivilegedServiceAction>))]
public enum PrivilegedServiceAction
{
    Start,
    Stop,
    Restart,
}

[JsonConverter(typeof(JsonStringEnumConverter<NginxSystemServiceAction>))]
public enum NginxSystemServiceAction
{
    Start,
    Stop,
    Restart,
    Reload,
    Enable,
    Disable,
    EnableAndStart,
    DisableAndStop,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProxyMihomoServiceAction>))]
public enum ProxyMihomoServiceAction
{
    DaemonReload,
    Enable,
    Disable,
    Start,
    Stop,
    Restart,
    TryRestart,
}

/// <summary>Closed SMB service verbs. The SMB Helper never accepts a service identifier.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SmbServiceAction>))]
public enum SmbServiceAction { Start, Stop, Restart, Reload }

/// <summary>Helper-side permission shape. It is not an HTTP contract and contains no native config text.</summary>
public sealed record SmbSharePermissionRequest(string Principal, string Access);

/// <summary>Helper-side share shape. Serialization is generated by the Server; arbitrary Samba syntax is impossible.</summary>
public sealed record SmbManagedShareRequest(string Id, string Name, string Path, string? Description, bool ReadOnly, bool Enabled,
    bool GuestAllowed, IReadOnlyList<SmbSharePermissionRequest> Permissions);

/// <summary>
/// Non-secret result of the fixed Windows SMB Server security baseline. The snapshot is a hash;
/// raw server configuration (including any administrator-owned null-session entries) never leaves
/// the LocalSystem Helper.
/// </summary>
public sealed record SmbWindowsServerSecuritySnapshot(string SnapshotHash, bool Smb1Enabled, bool Smb2Enabled,
    bool AuthenticatedUserSharingEnabled, bool NullSessionsDisabled, bool Compliant);

/// <summary>
/// Structured Linux Docker daemon proxy. The Helper validates every value again and can only write
/// it into its own fixed <c>docker.service</c> drop-in; no path, unit name, or command is accepted.
/// Proxy URLs may embed credentials, so the Helper never echoes them back.
/// </summary>
public sealed record DockerProxyConfiguration(string HttpProxy, string HttpsProxy, string NoProxy, bool Enabled = true);

/// <summary>Closed UFW rule verbs. Endpoint values are validated again by the Helper.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FirewallRuleAction>))]
public enum FirewallRuleAction { Allow, Deny, Reject, Limit }

[JsonConverter(typeof(JsonStringEnumConverter<FirewallRuleDirection>))]
public enum FirewallRuleDirection { In, Out }

[JsonConverter(typeof(JsonStringEnumConverter<FirewallRuleProtocol>))]
public enum FirewallRuleProtocol { Tcp, Udp, Any }

[JsonConverter(typeof(JsonStringEnumConverter<FirewallDefaultPolicy>))]
public enum FirewallDefaultPolicy { Allow, Deny, Reject }

/// <summary>Stable, non-secret failure classifications returned by a local Helper.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrivilegedProblemCode>))]
public enum PrivilegedProblemCode
{
    None,
    InvalidProtocol,
    InvalidRequest,
    UnsupportedOperation,
    DependencyMissing,
    InvalidPath,
    ResourceNotAllowed,
    NotFound,
    AccessDenied,
    Conflict,
    ContentTooLarge,
    HelperUnavailable,
    TimedOut,
    RestartRequired,
    InternalError,
}

/// <summary>
/// Local-only, strongly shaped Helper request. This is intentionally not an HTTP contract and
/// contains no host credential or generic process-execution fields.
/// </summary>
public sealed record PrivilegedOperationRequest(
    [property: JsonPropertyName("operation")] PrivilegedOperationKind Operation,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("destinationPath")] string? DestinationPath = null,
    [property: JsonPropertyName("newName")] string? NewName = null,
    [property: JsonPropertyName("fileName")] string? FileName = null,
    [property: JsonPropertyName("overwrite")] bool Overwrite = false,
    [property: JsonPropertyName("contentBase64")] string? ContentBase64 = null,
    [property: JsonPropertyName("fileAuthorizationSource")] PrivilegedFileAuthorizationSource? FileAuthorizationSource = null,
    [property: JsonPropertyName("unixMode")] int? UnixMode = null,
    [property: JsonPropertyName("recursive")] bool Recursive = false,
    [property: JsonPropertyName("unixOwner")] string? UnixOwner = null,
    [property: JsonPropertyName("unixGroup")] string? UnixGroup = null,
    /// <summary>Byte offset for FileUploadChunk or FileRead; other operations leave it null.</summary>
    [property: JsonPropertyName("offset")] long? Offset = null,
    [property: JsonPropertyName("readCount")] int? ReadCount = null,
    [property: JsonPropertyName("serviceId")] string? ServiceId = null,
    [property: JsonPropertyName("serviceAction")] PrivilegedServiceAction? ServiceAction = null,
    [property: JsonPropertyName("nginxServiceAction")] NginxSystemServiceAction? NginxServiceAction = null,
    [property: JsonPropertyName("componentId")] string? ComponentId = null,
    [property: JsonPropertyName("packageChange")] RelaxKonOS.Protocol.ManagedRuntimes.ComponentPackageChange? PackageChange = null,
    [property: JsonPropertyName("packageRecovery")] RelaxKonOS.Protocol.ManagedRuntimes.ComponentRecoveryRequest? PackageRecovery = null,
    [property: JsonPropertyName("archiveExpectation")] RelaxKonOS.Protocol.ManagedRuntimes.ComponentPinnedArchive? ArchiveExpectation = null,
    [property: JsonPropertyName("packageVersion")] string? PackageVersion = null,
    /// <summary>The frozen package identity an install must honour. Only the frozen install kind reads it.</summary>
    [property: JsonPropertyName("packageExpectation")] RelaxKonOS.Protocol.ManagedRuntimes.ComponentPackageTarget? PackageExpectation = null,
    [property: JsonPropertyName("proxyMihomoServiceAction")] ProxyMihomoServiceAction? ProxyMihomoServiceAction = null,
    [property: JsonPropertyName("linuxSystemProxy")] LinuxSystemProxyConfiguration? LinuxSystemProxy = null,
    [property: JsonPropertyName("firewallEnabled")] bool? FirewallEnabled = null,
    [property: JsonPropertyName("firewallNumberedStatus")] bool? FirewallNumberedStatus = null,
    [property: JsonPropertyName("firewallIncomingPolicy")] FirewallDefaultPolicy? FirewallIncomingPolicy = null,
    [property: JsonPropertyName("firewallOutgoingPolicy")] FirewallDefaultPolicy? FirewallOutgoingPolicy = null,
    [property: JsonPropertyName("firewallRuleAction")] FirewallRuleAction? FirewallRuleAction = null,
    [property: JsonPropertyName("firewallRuleDirection")] FirewallRuleDirection? FirewallRuleDirection = null,
    [property: JsonPropertyName("firewallRuleProtocol")] FirewallRuleProtocol? FirewallRuleProtocol = null,
    [property: JsonPropertyName("firewallSource")] string? FirewallSource = null,
    [property: JsonPropertyName("firewallDestination")] string? FirewallDestination = null,
    [property: JsonPropertyName("firewallPort")] string? FirewallPort = null,
    [property: JsonPropertyName("firewallRuleNumber")] int? FirewallRuleNumber = null,
    [property: JsonPropertyName("firewallCompanionRuleNumber")] int? FirewallCompanionRuleNumber = null,
    [property: JsonPropertyName("dockerProxy")] DockerProxyConfiguration? DockerProxy = null,
    /// <summary>Lifecycle action for the fixed Docker daemon unit. The unit name is a Helper
    /// constant, so a caller cannot aim this at another service.</summary>
    [property: JsonPropertyName("dockerServiceAction")] PrivilegedServiceAction? DockerServiceAction = null,
    [property: JsonPropertyName("smbServiceAction")] SmbServiceAction? SmbServiceAction = null,
    [property: JsonPropertyName("smbUsername")] string? SmbUsername = null,
    [property: JsonPropertyName("smbPassword")] string? SmbPassword = null,
    [property: JsonPropertyName("smbUserEnabled")] bool? SmbUserEnabled = null,
    [property: JsonPropertyName("smbShareId")] string? SmbShareId = null,
    [property: JsonPropertyName("smbShares")] IReadOnlyList<SmbManagedShareRequest>? SmbShares = null,
    [property: JsonPropertyName("smbShare")] SmbManagedShareRequest? SmbShare = null,
    [property: JsonPropertyName("smbExpectedSnapshot")] string? SmbExpectedSnapshot = null,
    [property: JsonPropertyName("systemAuthenticationUsername")] string? SystemAuthenticationUsername = null,
    [property: JsonPropertyName("systemAuthenticationPassword")] string? SystemAuthenticationPassword = null,
    [property: JsonPropertyName("hostAdministratorUsername")] string? HostAdministratorUsername = null,
    [property: JsonPropertyName("hostAdministratorUid")] string? HostAdministratorUid = null,
    [property: JsonPropertyName("environmentTarget")] SettingsTarget? EnvironmentTarget = null,
    [property: JsonPropertyName("environmentChange")] EnvironmentChangeSet? EnvironmentChange = null,
    [property: JsonPropertyName("timeZoneId")] string? TimeZoneId = null,
    [property: JsonPropertyName("hostName")] string? HostName = null,
    [property: JsonPropertyName("expectedRevision")] string? ExpectedRevision = null,
    [property: JsonPropertyName("operationId")] Guid? OperationId = null,
    [property: JsonPropertyName("correlation")] CorrelationContext? Correlation = null,
    [property: JsonPropertyName("version")] string Version = PrivilegedOperationProtocol.Version,
    [property: JsonPropertyName("managedRuntime")] ManagedRuntimeRequest? ManagedRuntime = null,
    [property: JsonPropertyName("tailscaleRecovery")] RelaxKonOS.Protocol.Tailscale.TailscaleRecoveryCommand? TailscaleRecovery = null,
    [property: JsonPropertyName("tailscaleChange")] RelaxKonOS.Protocol.Tailscale.TailscalePrepareRequest? TailscaleChange = null,
    /// <summary>Confirmed first-login plan. Only the begin verb reads it.</summary>
    [property: JsonPropertyName("tailscaleLogin")] RelaxKonOS.Protocol.Tailscale.TailscaleLoginCommand? TailscaleLogin = null,
    /// <summary>Original login identity for receipt, challenge and recovery verbs.</summary>
    [property: JsonPropertyName("tailscaleLoginIdentity")] RelaxKonOS.Protocol.Tailscale.TailscaleLoginIntent? TailscaleLoginIdentity = null,
    /// <summary>Confirmed login closure request. Only the close verb reads it.</summary>
    [property: JsonPropertyName("tailscaleLoginClose")] RelaxKonOS.Protocol.Tailscale.TailscaleLoginCloseRequest? TailscaleLoginClose = null,
    [property: JsonPropertyName("networkChange")] HostNetworkChange? NetworkChange = null,
    [property: JsonPropertyName("networkCheckpoint")] string? NetworkCheckpoint = null,
    [property: JsonPropertyName("processId")] int? ProcessId = null,
    [property: JsonPropertyName("processExpectedStartTime")] DateTimeOffset? ProcessExpectedStartTime = null,
    [property: JsonPropertyName("squidConfiguration")] RelaxKonOS.Protocol.GatewayProxy.ApplySquidConfigurationRequest? SquidConfiguration = null,
    [property: JsonPropertyName("squidServiceAction")] RelaxKonOS.Protocol.GatewayProxy.SquidServiceAction? SquidServiceAction = null,
    [property: JsonPropertyName("squidAccount")] RelaxKonOS.Protocol.GatewayProxy.SquidAccountMutation? SquidAccount = null,
    [property: JsonPropertyName("proxyServer")] RelaxKonOS.Protocol.ProxyServer.ProxyServerHelperRequest? ProxyServer = null,
    [property: JsonPropertyName("runtimeSourceId")] string? RuntimeSourceId = null,
    /// <summary>
    /// Host download route for the operations that fetch bytes (catalog refresh, runtime execution).
    /// The Server resolves it from the saved host preference; the Helper validates it again and it is
    /// never stored in a frozen plan, a ledger, or the runtime's own outbound proxy configuration.
    /// </summary>
    [property: JsonPropertyName("downloadProxy")] RelaxKonOS.Protocol.ManagedRuntimes.RuntimeDownloadProxy? DownloadProxy = null,
    [property: JsonPropertyName("runtimeLifecycle")] RelaxKonOS.Protocol.ManagedRuntimes.RuntimeLifecycleRequest? RuntimeLifecycle = null);

/// <summary>Versioned structured result returned by the local Helper.</summary>
public sealed record PrivilegedOperationResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("exitCode")] int ExitCode = 0,
    [property: JsonPropertyName("outputBase64")] string? OutputBase64 = null,
    /// <summary>Resulting length of an upload-session staging file after an append. Only
    /// <see cref="PrivilegedOperationKind.FileUploadChunk"/> sets it.</summary>
    [property: JsonPropertyName("offset")] long? Offset = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("problemCode")] PrivilegedProblemCode ProblemCode = PrivilegedProblemCode.None,
    [property: JsonPropertyName("hostEnvironment")] PrivilegedEnvironmentState? HostEnvironment = null,
    [property: JsonPropertyName("systemProxyCapabilities")] ProxySystemProxyCapabilities? SystemProxyCapabilities = null,
    [property: JsonPropertyName("hostTime")] HostTimeState? HostTime = null,
    [property: JsonPropertyName("tailscaleRecovery")] RelaxKonOS.Protocol.Tailscale.TailscaleRecoverySnapshot? TailscaleRecovery = null,
    [property: JsonPropertyName("tailscaleClosure")] RelaxKonOS.Protocol.Tailscale.TailscaleRecoveryClosure? TailscaleClosure = null,
    [property: JsonPropertyName("tailscaleControl")] RelaxKonOS.Protocol.Tailscale.TailscaleControlSnapshot? TailscaleControl = null,
    [property: JsonPropertyName("tailscaleResult")] RelaxKonOS.Protocol.Tailscale.TailscaleControlResult? TailscaleResult = null,
    [property: JsonPropertyName("tailscaleLoginReceipt")] RelaxKonOS.Protocol.Tailscale.TailscaleLoginReceipt? TailscaleLoginReceipt = null,
    [property: JsonPropertyName("tailscaleLoginObservation")] RelaxKonOS.Protocol.Tailscale.TailscaleLoginObservation? TailscaleLoginObservation = null,
    /// <summary>Transient browser challenge. The URL is delivered only by the dedicated challenge verb.</summary>
    [property: JsonPropertyName("tailscaleLoginChallenge")] RelaxKonOS.Protocol.Tailscale.TailscaleLoginChallenge? TailscaleLoginChallenge = null,
    [property: JsonPropertyName("tailscaleLoginRecovery")] RelaxKonOS.Protocol.Tailscale.TailscaleLoginRecoverySnapshot? TailscaleLoginRecovery = null,
    [property: JsonPropertyName("tailscaleLoginClosure")] RelaxKonOS.Protocol.Tailscale.TailscaleLoginClosure? TailscaleLoginClosure = null,
    [property: JsonPropertyName("tailscaleStatus")] RelaxKonOS.Protocol.Tailscale.TailscaleStatusDto? TailscaleStatus = null,
    [property: JsonPropertyName("hostNetwork")] HostNetworkSnapshot? HostNetwork = null,
    [property: JsonPropertyName("networkCheckpoint")] string? NetworkCheckpoint = null,
    [property: JsonPropertyName("hostIdentity")] HostIdentityState? HostIdentity = null,
    [property: JsonPropertyName("systemAuthenticationResult")] SystemAuthenticationResult? SystemAuthenticationResult = null,
    [property: JsonPropertyName("hostAdministratorEligible")] bool? HostAdministratorEligible = null,
    [property: JsonPropertyName("nginxRunning")] bool? NginxRunning = null,
    /// <summary>Read-only package facts. Only <see cref="PrivilegedOperationKind.ComponentPackageQuery"/> sets it.</summary>
    [property: JsonPropertyName("pinnedArchives")] IReadOnlyList<RelaxKonOS.Protocol.ManagedRuntimes.ComponentPinnedArchive>? PinnedArchives = null,
    [property: JsonPropertyName("componentArchive")] RelaxKonOS.Protocol.ManagedRuntimes.ComponentArchiveHostSnapshot? ComponentArchive = null,
    [property: JsonPropertyName("componentPackage")] RelaxKonOS.Protocol.ManagedRuntimes.ComponentPackageResolution? ComponentPackage = null,
    [property: JsonPropertyName("componentProcess")] ManagedProcessSnapshot? ComponentProcess = null,
    [property: JsonPropertyName("processSnapshot")] HostProcessSnapshotDto? ProcessSnapshot = null,
    [property: JsonPropertyName("processTermination")] KillProcessResultDto? ProcessTermination = null,
    [property: JsonPropertyName("processIconContentType")] string? ProcessIconContentType = null,
    [property: JsonPropertyName("version")] string Version = PrivilegedOperationProtocol.Version);

/// <summary>Fixed Linux proxy writers; no executable, arbitrary variable, path, or target user can be supplied.</summary>
public sealed record LinuxSystemProxyConfiguration(bool Enabled, string Host, int Port, bool UseDefaultBypass, string BypassList, bool Enforce = false);
