using RelaxKonOS.Protocol.ProxyServer;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>Local Server/Helper material only. Never exposed by management reads or operation records.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SingBoxCurrentAuthorization(ProxyServerMaterial? Published, DateTimeOffset ValidUntil) : IRuntimeAuthorizationGrant
{
    public static string RuntimeId => "sing-box";
}

/// <summary>A typed, short-lived callback grant. Runtime identity also binds the channel authentication.</summary>
public interface IRuntimeAuthorizationGrant
{
    static abstract string RuntimeId { get; }
    DateTimeOffset ValidUntil { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoRuntimeListener(string Address, int Port, bool Udp = false);

/// <summary>Protected callback material only; YAML and controller secrets must never enter public reads or ledgers.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoPublishedRuntime([property: JsonRequired] string Yaml,
    [property: JsonRequired] IReadOnlyList<string> RequiredBuildTags,
    [property: JsonRequired] int ControllerPort, [property: JsonRequired] string ControllerSecret,
    [property: JsonRequired] IReadOnlyList<MihomoRuntimeListener> Listeners, bool NetworkTransitionRequired = false)
{
    public override string ToString() => "MihomoPublishedRuntime { protected material }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoCurrentAuthorization(DateTimeOffset ValidUntil, MihomoPublishedRuntime? Published) : IRuntimeAuthorizationGrant
{
    public static string RuntimeId => "mihomo";
}

/// <summary>One named publication only. A missing side denies that side; revoked candidate material must
/// not prevent independently authorized compensation to the previous publication.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoConfigurationAuthorization([property: JsonRequired] Guid OperationId,
    [property: JsonRequired] DateTimeOffset ValidUntil, [property: JsonRequired] MihomoPublishedRuntime? Previous,
    [property: JsonRequired] MihomoPublishedRuntime? Candidate) : IRuntimeAuthorizationGrant
{
    public static string RuntimeId => "mihomo";
    public override string ToString() => $"MihomoConfigurationAuthorization {{ OperationId = {OperationId}, protected material }}";
}

/// <summary>One operation's ephemeral local channel. No address, path, user, or executable is selectable.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAuthorizationChannel(Guid ChannelId, Guid OperationId, string RuntimeId, string Secret)
{
    public override string ToString() => $"RuntimeAuthorizationChannel {{ ChannelId = {ChannelId}, OperationId = {OperationId}, RuntimeId = {RuntimeId}, Secret = [redacted] }}";
}
