using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Protocol.Privileged;

[JsonConverter(typeof(JsonStringEnumConverter<ManagedRuntime>))]
public enum ManagedRuntime { Nginx, Frpc, Frps }

[JsonConverter(typeof(JsonStringEnumConverter<ManagedRuntimeAction>))]
public enum ManagedRuntimeAction { Install, Uninstall, Start, Stop, Restart, Reload, Test, Status }

/// <summary>Closed Windows runtime verbs. Executables, command lines and environments are Helper-owned.</summary>
public sealed record ManagedRuntimeRequest(
    ManagedRuntime Runtime, ManagedRuntimeAction Action,
    string? Version = null, Guid? ProfileId = null, string? ArchivePath = null,
    FrpcServiceConfiguration? Client = null, FrpsServiceConfiguration? Server = null, string? AppliedIdentity = null);

public sealed record FrpcServiceConfiguration(string Host, int Port, TunnelTlsMode TlsMode,
    string? Token, IReadOnlyList<FrpServiceProxy> Proxies);
public sealed record FrpServiceProxy(string Name, TunnelProtocol Protocol, string LocalHost, int LocalPort,
    int? RemotePort, string? Domain, bool Encryption, bool Compression);
public sealed record FrpsServiceConfiguration(string BindAddress, int BindPort,
    IReadOnlyList<TunnelPortRangeDto> AllowPorts, int? HttpPort, int? HttpsPort, bool ForceTls,
    string Token, bool DashboardEnabled, string DashboardAddress, int? DashboardPort,
    string? DashboardUser, string? DashboardPassword);

public sealed record ManagedProcessSnapshot(bool Running, bool Connected, bool AuthenticationFailed,
    DateTimeOffset? StartedAt, IReadOnlyList<TunnelLogEntryDto> Logs, string? AppliedIdentity = null);

/// <summary>Administrator-owned archive pins, shared by the installer and Helper configuration.</summary>
public sealed record WindowsFrpRelease(string Version, string Rid, string Url, string Sha256, string ArchiveFormat);

public static class WindowsManagedRuntimeDefaults
{
    public static string NginxRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "webserver", "nginx");
    public static string PrivateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "privileged-runtimes");
    public static IReadOnlyList<WindowsFrpRelease> FrpReleases { get; } =
    [
        new("v0.71.0", "win-x64", "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_amd64.zip", "9e5062e3e5cf07e67144a3a4acf175ef6a2486f3605dd6cf288bae34ab39819f", "zip"),
        new("v0.71.0", "win-arm64", "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_arm64.zip", "b56a5c2a1a2a55d11bc27aeef6edabd39f3d194360ea66660cc27281b502cb1c", "zip"),
    ];
}
