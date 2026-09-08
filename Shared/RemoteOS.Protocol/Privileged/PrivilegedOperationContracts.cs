using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.Privileged;

/// <summary>Versioning and size limits for the local Helper protocol.</summary>
public static class PrivilegedOperationProtocol
{
    public const string Version = "1.0";
    public const int MaximumRequestBytes = 16 * 1024 * 1024;
    public const int MaximumFileContentBytes = 12 * 1024 * 1024;
}

/// <summary>
/// Closed set of operations understood by the local Helper. Do not add a command, executable,
/// shell, argument list, working directory, or environment operation to this enum or request.
/// Those values would turn the Helper into a general elevation API.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrivilegedOperationKind>))]
public enum PrivilegedOperationKind
{
    FileRead,
    FileWrite,
    FileDelete,
    FileRename,
    FileMove,
    FileCopy,
    FileUpload,
    FileCreateDirectory,
    NativeServiceAction,
    NginxSystemServiceAction,
    NginxPackageInstall,
    NginxPackageUninstall,
    NginxWriteManagedFile,
    NginxMoveManagedFile,
    NginxDeleteManagedFile,
    ProxyMihomoServiceAction,
    ProxyMihomoInstallSystemService,
    ProxyMihomoRemoveSystemService,
    GitPackageInstall,
    FirewallUfwStatus,
    FirewallUfwSetEnabled,
    FirewallUfwSetDefaults,
    FirewallUfwCreateRule,
    FirewallUfwReplaceRule,
    FirewallUfwDeleteRule,
}

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
    InvalidPath,
    ResourceNotAllowed,
    NotFound,
    AccessDenied,
    Conflict,
    ContentTooLarge,
    HelperUnavailable,
    TimedOut,
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
    [property: JsonPropertyName("serviceId")] string? ServiceId = null,
    [property: JsonPropertyName("serviceAction")] PrivilegedServiceAction? ServiceAction = null,
    [property: JsonPropertyName("nginxServiceAction")] NginxSystemServiceAction? NginxServiceAction = null,
    [property: JsonPropertyName("packageVersion")] string? PackageVersion = null,
    [property: JsonPropertyName("proxyMihomoServiceAction")] ProxyMihomoServiceAction? ProxyMihomoServiceAction = null,
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
    [property: JsonPropertyName("operationId")] Guid? OperationId = null,
    [property: JsonPropertyName("version")] string Version = PrivilegedOperationProtocol.Version);

/// <summary>Versioned structured result returned by the local Helper.</summary>
public sealed record PrivilegedOperationResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("exitCode")] int ExitCode = 0,
    [property: JsonPropertyName("outputBase64")] string? OutputBase64 = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("problemCode")] PrivilegedProblemCode ProblemCode = PrivilegedProblemCode.None,
    [property: JsonPropertyName("version")] string Version = PrivilegedOperationProtocol.Version);
