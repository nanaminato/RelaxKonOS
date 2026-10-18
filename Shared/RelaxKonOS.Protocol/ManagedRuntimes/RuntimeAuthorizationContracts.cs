using RelaxKonOS.Protocol.ProxyServer;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>Local Server/Helper material only. Never exposed by management reads or operation records.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SingBoxCurrentAuthorization(ProxyServerMaterial? Published, DateTimeOffset ValidUntil);

/// <summary>One operation's ephemeral local channel. No address, path, user, or executable is selectable.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAuthorizationChannel(Guid ChannelId, Guid OperationId, string Secret);
