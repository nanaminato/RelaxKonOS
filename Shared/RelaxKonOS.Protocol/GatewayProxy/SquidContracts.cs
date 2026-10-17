using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.GatewayProxy;

public static class GatewayProxyRoutes
{
    public const string Root = $"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/gateway-proxy/squid";
    public const string Configuration = Root + "/configuration";
    public const string Status = Root + "/status";
    public const string Preview = Root + "/configuration/preview";
    public const string Apply = Root + "/configuration/apply";
    public const string Validate = Root + "/configuration/validate";
    public const string Lifecycle = Root + "/lifecycle";
}

/// <summary>Single explicit forward proxy. No native directives, paths, credentials or TLS interception.</summary>
public sealed record SquidConfiguration
{
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 3128;
    public bool AllowRemoteClients { get; init; }
    public string[] AllowedNetworks { get; init; } = ["127.0.0.1/32", "::1/128"];
    public string[] AllowedDomains { get; init; } = [];
    public string[] DeniedDomains { get; init; } = [];
    public SquidHttpUpstream? Upstream { get; init; }
}

public sealed record SquidHttpUpstream(string Host, int Port);
public sealed record ApplySquidConfigurationRequest(SquidConfiguration Configuration, bool AdoptExistingConfiguration = false);
public sealed record SquidConfigurationPreview(bool Valid, string? Configuration, string[] Errors);
public sealed record SquidNativeValidation(bool Valid, string? ProblemCode, string Diagnostics);
[JsonConverter(typeof(JsonStringEnumConverter<SquidServiceAction>))]
public enum SquidServiceAction { Start, Stop, Restart, Reload, Enable, Disable, Install, Uninstall }
public sealed record SquidLifecycleRequest(SquidServiceAction Action);
public sealed record SquidStatus(bool Supported, bool Installed, bool Running, bool Enabled, bool Managed, string ConfigurationRevision, [property: JsonRequired] bool PackageManagementSupported, bool RecoveryRequired = false);

/// <summary>Last submitted editor state and publication receipt; never a runtime applied claim.</summary>
public sealed record SquidConfigurationState(SquidConfiguration? Configuration, long DesiredRevision,
    long? PublishedRevision, string? PublishedConfigurationHash, string? LastProblemCode);
public sealed record SquidConfigurationSnapshot(SquidConfigurationState State, SquidStatus Status, bool MatchesPublishedConfiguration);
