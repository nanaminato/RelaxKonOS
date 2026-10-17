using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.BackupRecovery;
using RelaxKonOS.Server.EventAlerts;

namespace RelaxKonOS.Server.HostMode;

/// <summary>
/// Builds the authenticated deployment descriptor exactly once per login. The platform is an input
/// instead of a runtime probe so the advertised capability set is directly testable: clients decide
/// which applications to offer from these strings, and a capability that depends on the executing
/// operating system cannot be verified on the other one.
/// </summary>
public static class ServerDescriptorFactory
{
    public static ServerDescriptorDto Create(HostPlatformKind platform, IServerModeResolver serverMode,
        EventAlertsOptions eventAlerts, IBackupRecoveryKeyProvider backupRecovery)
    {
        var isWindows = platform == HostPlatformKind.Windows;
        var capabilities = new List<string>
        {
            ServerCapabilities.Files,
            ServerCapabilities.Metrics,
            ServerCapabilities.Processes,
            ServerCapabilities.Terminal,
            ServerCapabilities.Git,
        };
        if (!isWindows && serverMode.Supports(ServerHostFeature.Firewall)) capabilities.Add(ServerCapabilities.PosixPermissions);
        if (serverMode.Supports(ServerHostFeature.Firewall)) capabilities.Add(ServerCapabilities.Firewall);

        var host = serverMode.Describe();
        if (host.Capabilities.Guardian) capabilities.Add(ServerCapabilities.Guardian);
        if (host.Capabilities.Docker) capabilities.Add(ServerCapabilities.Docker);
        if (host.Capabilities.FileServices) capabilities.Add(ServerCapabilities.FileServices);
        if (host.Capabilities.WebServer) capabilities.Add(ServerCapabilities.WebServer);
        if (host.Capabilities.Certificates) capabilities.Add(ServerCapabilities.Certificates);
        if (host.Capabilities.Tunnels) capabilities.Add(ServerCapabilities.Tunnels);
        if (host.Capabilities.Proxy) capabilities.Add(ServerCapabilities.Proxy);
        // Squid exists only on Unix-like hosts, but the gateway contract is answerable everywhere:
        // a host without Squid reports Supported=false, which is how the client explains the
        // situation instead of the application silently disappearing from its shell.
        if (host.Capabilities.Proxy) capabilities.Add(ServerCapabilities.GatewayProxy);
        if (host.Capabilities.Proxy && serverMode.Mode == RelaxKonOS.Protocol.Common.ServerMode.System) capabilities.Add(ServerCapabilities.ProxyServer);
        if (host.Capabilities.ApplicationDeployments) capabilities.Add(ServerCapabilities.ApplicationDeployments);
        if (eventAlerts.Enabled) capabilities.Add(ServerCapabilities.EventAlerts);
        if (host.Capabilities.ApplicationDeployments && backupRecovery.Availability.Available)
            capabilities.Add(ServerCapabilities.BackupRecovery);
        return new ServerDescriptorDto(platform, capabilities, host);
    }
}
