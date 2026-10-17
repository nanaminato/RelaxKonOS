using System.Text.Json.Serialization;
using System.Text.Json;

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
    public const string Accounts = Root + "/accounts";
}

/// <summary>Single explicit forward proxy. No native directives, paths or TLS interception. Credentials use the separate account contract.</summary>
public sealed record SquidConfiguration
{
    [JsonRequired] public SquidAccessMode AccessMode { get; init; } = SquidAccessMode.IpWhitelist;
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

[JsonConverter(typeof(SquidAccessModeJsonConverter))]
public enum SquidAccessMode { IpWhitelist, Password, PasswordAndIpWhitelist }
public sealed class SquidAccessModeJsonConverter() : JsonStringEnumConverter<SquidAccessMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

public sealed record SquidAccount(string Username, bool Enabled);
[JsonConverter(typeof(JsonStringEnumConverter<SquidAccountAction>))]
public enum SquidAccountAction { Create, SetPassword, SetEnabled, Delete }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SquidAccountMutation(SquidAccountAction Action, string Username, string? Password = null, bool? Enabled = null)
{
    public override string ToString() => $"SquidAccountMutation {{ Action = {Action}, Username = {Username}, Password = [redacted], Enabled = {Enabled} }}";
}

public static partial class SquidAccountValidation
{
    public static bool ValidUsername(string? value) => value is not null && UsernamePattern().IsMatch(value);
    public static bool ValidPassword(string? value) => value is { Length: >= 8 and <= 128 } && !value.Any(char.IsControl);
    public static bool Valid(SquidAccountMutation? request) => request is not null && ValidUsername(request.Username) && request.Action switch
    {
        SquidAccountAction.Create or SquidAccountAction.SetPassword => ValidPassword(request.Password) && request.Enabled is null,
        SquidAccountAction.SetEnabled => request.Password is null && request.Enabled is not null,
        SquidAccountAction.Delete => request.Password is null && request.Enabled is null,
        _ => false,
    };
    [System.Text.RegularExpressions.GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UsernamePattern();
}
