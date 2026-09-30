using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Tunnels;

/// <summary>In-memory identity of the exact desired profile, definitions and protected token applied to frpc.
/// It never exposes credentials or infers an applied revision from a connection log.</summary>
internal static class FrpcAppliedState
{
    internal static string Fingerprint(TunnelServerProfile profile, IEnumerable<TunnelDefinition> definitions, string? protectedToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            profile.Id, profile.Revision,
            Definitions = definitions.OrderBy(item => item.Id).Select(item => new { item.Id, item.Revision }).ToArray(),
            Token = protectedToken is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(protectedToken)))
        }))));

    internal static TunnelConnectionState Project(TunnelConnectionState runtime, string? applied, string desired, bool enabled)
    {
        if (runtime is not (TunnelConnectionState.Starting or TunnelConnectionState.Connected)) return runtime;
        if (applied is null) return TunnelConnectionState.Unknown;
        if (applied != desired) return TunnelConnectionState.SavedNotApplied;
        return enabled ? runtime : TunnelConnectionState.Disconnected;
    }
}
