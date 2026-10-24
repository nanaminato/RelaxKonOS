using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// The host download route for one managed-runtime operation. The Server resolves it from the saved
/// host download-proxy preference, so it is neither a client choice nor process environment state, and
/// the Helper validates it again before any socket is opened. It deliberately travels outside the
/// frozen plan and the idempotency hash: changing the route must not invalidate an accepted install.
/// The runtime kernel's own outbound proxy is a different setting and never inherits this value.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeDownloadProxy(string Endpoint, IReadOnlyList<string> Bypass);
