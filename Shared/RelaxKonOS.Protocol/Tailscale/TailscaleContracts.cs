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
    public const string Login = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/tailscale/login";
    public const string LoginOperations = Login + "/operations";
    public const string LoginOperation = LoginOperations + "/{operationId:guid}";
    public const string LoginExecute = LoginOperation + "/execute";
    public const string LoginChallenge = LoginOperation + "/challenge";
    public const string LoginRecovery = LoginOperation + "/recovery";
    public const string LoginClose = LoginRecovery + "/close";
}

public static class TailscaleAccess
{
    public const string Resource = "host/tailscale";
}

/// <summary>Owner is the tailnet account login name; it is only populated when the peer list grant exists.</summary>
public sealed record TailscalePeerDto(string Id, string HostName, string DnsName,
    IReadOnlyList<string> Addresses, bool Online, bool ExitNode,
    string? Os = null, IReadOnlyList<string>? Tags = null, bool ExitNodeOption = false,
    bool Expired = false, DateTimeOffset? KeyExpiry = null, DateTimeOffset? LastSeen = null,
    string? Owner = null);

/// <summary>Display identity of the host's own Tailscale account; never a credential.</summary>
public sealed record TailscaleAccountDto(string LoginName, string DisplayName);

/// <summary>
/// Account, tailnet and peer identities stay gated by HostNetworkChange/host/tailscale. The
/// connection state and version are readable by any authenticated account.
/// </summary>
public sealed record TailscaleStatusDto(string State, string? Version = null,
    string? BackendState = null, TailscalePeerDto? Self = null,
    IReadOnlyList<TailscalePeerDto>? Peers = null, string? ProblemCode = null, bool PeersRestricted = false,
    string? TailnetName = null, string? MagicDnsSuffix = null, TailscaleAccountDto? Account = null,
    TailscalePeerDto? ExitNode = null);

/// <summary>The official client menu is a fixed, closed set of preferences; arbitrary flags are never accepted.</summary>
public sealed record TailscalePreferences(bool AcceptDns, bool AcceptRoutes, bool ShieldsUp,
    string? ExitNode, bool ExitNodeAllowLanAccess, bool Unattended, bool AutoUpdate, bool AdvertiseExitNode);
