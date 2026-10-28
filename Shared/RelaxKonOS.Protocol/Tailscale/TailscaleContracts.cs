using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Tailscale;

public static class TailscaleEndpoints
{
    public const string Control = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/tailscale/control";
    public const string Operations = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/tailscale/operations";
    public const string Operation = Operations + "/{operationId:guid}";
    public const string Execute = Operation + "/execute";
    public const string Recovery = Operation + "/recovery";
    public const string Close = Recovery + "/close";
    public const string Status = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/tailscale/status";
}

public static class TailscaleAccess
{
    public const string Resource = "host/tailscale";
}

public sealed record TailscalePeerDto(string Id, string HostName, string DnsName,
    IReadOnlyList<string> Addresses, bool Online, bool ExitNode);

public sealed record TailscaleStatusDto(string State, string? Version = null,
    string? BackendState = null, TailscalePeerDto? Self = null,
    IReadOnlyList<TailscalePeerDto>? Peers = null, string? ProblemCode = null, bool PeersRestricted = false);
