using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.BackupRecovery;
using RelaxKonOS.Server.EventAlerts;

namespace RelaxKonOS.Server.HostMode;

/// <summary>
/// Builds the authenticated deployment descriptor exactly once per login. The platform is an input
/// instead of a runtime probe so the advertised capability set is directly testable: clients decide
/// which applications to offer from these strings, and a capability that depends on the executing
/// operating system cannot be verified on the other one.
///
/// Module capabilities are an input too: the Host still decides what it can serve, but a capability
/// whose module is not part of this process's startup snapshot must never be advertised. Without
/// that intersection a client would offer an application whose routes were never mapped.
/// </summary>
public static class ServerDescriptorFactory
{
    public static ServerDescriptorDto Create(HostPlatformKind platform, IServerModeResolver serverMode,
        EventAlertsOptions eventAlerts, IBackupRecoveryKeyProvider backupRecovery, IReadOnlySet<string> enabledModuleCapabilities)
    {
        var isWindows = platform == HostPlatformKind.Windows;
        // Deployment mode still owns the upper bound: a module cannot grant what the host refuses.
        var host = ApplyModules(serverMode.Describe(), enabledModuleCapabilities);
        var hostCapabilities = host.Capabilities;

        // The flat list is derived from the same booleans the client sees in the Host block, so the
        // two descriptions of one capability cannot disagree.
        var capabilities = new List<string> { ServerCapabilities.Files };
        // Component lifecycle is still a Core surface (its adapters are registered by the Host), so
        // it follows the deployment mode instead of a module.
        if (serverMode.Supports(ServerHostFeature.PrivilegedOperations) && serverMode.Supports(ServerHostFeature.AgentInstallation))
            capabilities.Add(ServerCapabilities.Components);
        if (hostCapabilities.Metrics) capabilities.Add(ServerCapabilities.Metrics);
        if (hostCapabilities.Processes) capabilities.Add(ServerCapabilities.Processes);
        if (hostCapabilities.Git) capabilities.Add(ServerCapabilities.Git);
        if (hostCapabilities.Terminal) capabilities.Add(ServerCapabilities.Terminal);
        if (hostCapabilities.Guardian) capabilities.Add(ServerCapabilities.Guardian);
        if (hostCapabilities.Docker) capabilities.Add(ServerCapabilities.Docker);
        if (hostCapabilities.Firewall) capabilities.Add(ServerCapabilities.Firewall);
        if (hostCapabilities.FileServices) capabilities.Add(ServerCapabilities.FileServices);
        if (hostCapabilities.WebServer) capabilities.Add(ServerCapabilities.WebServer);
        if (hostCapabilities.Certificates) capabilities.Add(ServerCapabilities.Certificates);
        if (hostCapabilities.Tunnels) capabilities.Add(ServerCapabilities.Tunnels);
        if (hostCapabilities.Proxy) capabilities.Add(ServerCapabilities.Proxy);
        // Squid exists only on Unix-like hosts, but the gateway contract is answerable everywhere:
        // a host without Squid reports Supported=false, which is how the client explains the
        // situation instead of the application silently disappearing from its shell.
        if (hostCapabilities.Proxy) capabilities.Add(ServerCapabilities.GatewayProxy);
        if (hostCapabilities.Proxy && serverMode.Mode == RelaxKonOS.Protocol.Common.ServerMode.System) capabilities.Add(ServerCapabilities.ProxyServer);
        if (hostCapabilities.ApplicationDeployments) capabilities.Add(ServerCapabilities.ApplicationDeployments);
        if (!isWindows && serverMode.Supports(ServerHostFeature.Firewall)) capabilities.Add(ServerCapabilities.PosixPermissions);
        if (eventAlerts.Enabled) capabilities.Add(ServerCapabilities.EventAlerts);
        if (hostCapabilities.ApplicationDeployments && backupRecovery.Availability.Available)
            capabilities.Add(ServerCapabilities.BackupRecovery);
        return new ServerDescriptorDto(platform, capabilities, host);
    }

    /// <summary>
    /// Removes every module-owned capability that is not part of the startup snapshot. Both the
    /// login descriptor and the authenticated capability route go through this method, so a client
    /// can never read two different answers about whether an application has a server behind it.
    /// </summary>
    public static ServerCapabilitiesDto ApplyModules(ServerCapabilitiesDto host, IReadOnlySet<string> enabledModuleCapabilities)
    {
        var capabilities = host.Capabilities;
        return host with
        {
            Capabilities = capabilities with
            {
                Terminal = capabilities.Terminal && enabledModuleCapabilities.Contains(ServerCapabilities.Terminal),
                Git = capabilities.Git && enabledModuleCapabilities.Contains(ServerCapabilities.Git),
                Metrics = capabilities.Metrics && enabledModuleCapabilities.Contains(ServerCapabilities.Metrics),
                Processes = capabilities.Processes && enabledModuleCapabilities.Contains(ServerCapabilities.Processes),
                Firewall = capabilities.Firewall && enabledModuleCapabilities.Contains(ServerCapabilities.Firewall),
                Docker = capabilities.Docker && enabledModuleCapabilities.Contains(ServerCapabilities.Docker),
                WebServer = capabilities.WebServer && enabledModuleCapabilities.Contains(ServerCapabilities.WebServer),
                Certificates = capabilities.Certificates && enabledModuleCapabilities.Contains(ServerCapabilities.Certificates),
                // One host flag covers three applications (engine, Squid gateway, sing-box proxy
                // server); the flat list below projects it, and the proxy server stays bounded by
                // deployment mode (System only) in the mode description.
                Proxy = capabilities.Proxy && enabledModuleCapabilities.Contains(ServerCapabilities.Proxy),
            },
        };
    }
}
