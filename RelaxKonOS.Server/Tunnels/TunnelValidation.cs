using System.Net;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Tunnels;

internal static partial class TunnelValidation
{
    private const int MaxProfilesPerUser = 32;
    private const int MaxTunnelsPerProfile = 128;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$")]
    private static partial Regex NamePattern();
    [GeneratedRegex("^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(?:\\.(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?))*$")]
    private static partial Regex DomainPattern();

    public static string? ValidateProfile(string name, string host, int port, TunnelAuthKind authKind, TunnelRuntimeMode runtimeMode, string? externalPath)
    {
        if (!NamePattern().IsMatch(name) || !IsHost(host) || !IsPort(port)) return "tunnel.profile_invalid";
        if (authKind is not TunnelAuthKind.None and not TunnelAuthKind.Token) return "tunnel.auth_unsupported";
        if (runtimeMode == TunnelRuntimeMode.External && string.IsNullOrWhiteSpace(externalPath)) return "tunnel.external_path_required";
        if (runtimeMode == TunnelRuntimeMode.External && !Path.IsPathFullyQualified(externalPath!)) return "tunnel.external_path_invalid";
        return null;
    }

    public static string? ValidateDefinition(string name, TunnelProtocol protocol, string localHost, int localPort, int? remotePort, string? domain)
    {
        if (!NamePattern().IsMatch(name) || !IsHost(localHost) || !IsPort(localPort)) return "tunnel.definition_invalid";
        if (protocol is TunnelProtocol.Tcp or TunnelProtocol.Udp)
            return remotePort is > 0 and <= 65535 && string.IsNullOrWhiteSpace(domain) ? null : "tunnel.remote_port_required";
        if (protocol is TunnelProtocol.Http or TunnelProtocol.Https)
            return remotePort is null && IsDomain(domain) ? null : "tunnel.domain_required";
        return "tunnel.protocol_unsupported";
    }

    public static bool IsPort(int port) => port is > 0 and <= 65535;
    public static bool IsDomain(string? domain) => !string.IsNullOrWhiteSpace(domain) && DomainPattern().IsMatch(domain.TrimEnd('.'));
    private static bool IsHost(string? host) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        (IPAddress.TryParse(host, out _) || DomainPattern().IsMatch(host.TrimEnd('.')));
}
