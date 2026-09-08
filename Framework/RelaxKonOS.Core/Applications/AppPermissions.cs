namespace RelaxKonOS.Core.Applications;

/// <summary>Capabilities an application can request from the RelaxKonOS host.</summary>
public static class AppPermissions
{
    public const string ServerFilesRead = "server.files.read";
    public const string ServerFilesWrite = "server.files.write";
    public const string DesktopWallpaperWrite = "desktop.wallpaper.write";
    public const string ServerMetricsRead = "server.metrics.read";
    public const string ServerProcessesManage = "server.processes.manage";
    public const string ServerServicesManage = "server.services.manage";
    public const string ServerNetworkRead = "server.network.read";
    public const string ServerNetworkConfigure = "server.network.configure";
    public const string ServerPowerManage = "server.power.manage";
    public const string ServerDockerRead = "server.docker.read";
    public const string ServerDockerManage = "server.docker.manage";
    public const string ServerGuardianRead = "server.guardian.read";
    public const string ServerGuardianManage = "server.guardian.manage";
    public const string ServerFirewallRead = "server.firewall.read";
    public const string ServerFirewallManage = "server.firewall.manage";
    public const string ServerCertificatesRead = "server.certificates.read";
    public const string ServerCertificatesManage = "server.certificates.manage";
    public const string ServerWebServersRead = "server.webservers.read";
    public const string ServerWebServersManage = "server.webservers.manage";
    public const string ServerGitRead = "server.git.read";
    public const string ServerGitManage = "server.git.manage";
    public const string ServerTunnelsRead = "server.tunnels.read";
    public const string ServerTunnelsManage = "server.tunnels.manage";
    public const string ServerRegistryRead = "server.registry.read";
    public const string ServerRegistryWrite = "server.registry.write";
    public const string ServerProxyRead = "server.proxy.read";
    public const string ServerProxyManage = "server.proxy.manage";
    public const string ServerProxyProfileRead = "server.proxy.profile.read";
    public const string ServerProxyProfileManage = "server.proxy.profile.manage";
    public const string ServerProxyConnectionRead = "server.proxy.connection.read";
    public const string ServerProxyConnectionClose = "server.proxy.connection.close";
    public const string ServerProxyTunRead = "server.proxy.tun.read";
    public const string ServerProxyTunManage = "server.proxy.tun.manage";
    public const string ServerProxyRuntimeRead = "server.proxy.runtime.read";
    public const string ServerProxyRuntimeManage = "server.proxy.runtime.manage";
    public const string ServerProxyRecoveryExecute = "server.proxy.recovery.execute";

    /// <summary>
    /// English source metadata for every capability. Client UI resolves each value through
    /// <c>permission.{id}.*</c>; these values remain the SDK-safe English fallback.
    /// </summary>
    public static IReadOnlyList<AppPermissionDefinition> All { get; } =
    [
        new(ServerFilesRead, "Read server files", "Read files, directory structure, and basic metadata on the server.", "server_files"),
        new(ServerFilesWrite, "Modify server files", "Create, upload, edit, rename, and delete server files. This does not include changing file ownership or system permissions.", "server_files"),
        new(ServerProcessesManage, "Manage server processes", "View, start, stop, or terminate server processes. This permission includes reading the process list.", "server_management"),
        new(ServerServicesManage, "Manage system services", "View, start, stop, or restart services on the server.", "server_management"),
        new(ServerNetworkRead, "Read server network information", "Read network interfaces, IP addresses, routes, and connection state.", "server_network"),
        new(ServerNetworkConfigure, "Configure server network", "Change server IP addresses, routes, DNS, and other network configuration. It does not grant unrelated management capabilities.", "server_network"),
        new(ServerPowerManage, "Perform server power operations", "Request high-impact server operations such as restart or shutdown.", "server_management"),
        new(DesktopWallpaperWrite, "Change desktop wallpaper", "Allow this application to change the current workspace wallpaper through RelaxKonOS.", "desktop_workspace"),
        new(ServerMetricsRead, "Read server performance metrics", "Read aggregate CPU, memory, disk, network, and GPU metrics from the server.", "server_monitoring"),
        new(ServerDockerRead, "Read Docker resources", "View the server's local Docker status, containers, images, networks, volumes, and safe diagnostics.", "server_management"),
        new(ServerDockerManage, "Manage Docker resources", "Create and change local Docker containers, images, and Compose stacks on the server.", "server_management"),
        new(ServerGuardianRead, "Read guardian workloads", "View the Guardian Agent, its workloads, states, and sanitized logs.", "server_management"),
        new(ServerGuardianManage, "Manage guardian workloads", "Create and control workloads supervised by the Guardian Agent.", "server_management"),
        new(ServerFirewallRead, "Read firewall configuration", "View the Linux server firewall status, defaults, and rules.", "server_network"),
        new(ServerFirewallManage, "Manage firewall configuration", "Change the Linux server firewall status, default policies, and rules.", "server_network"),
        new(ServerCertificatesRead, "Read TLS certificates", "View TLS/SSL certificate metadata, issuance status, and renewal state on the server.", "server_management"),
        new(ServerCertificatesManage, "Manage TLS certificates", "Request, renew, deploy, revoke, and delete TLS certificates on the server.", "server_management"),
        new(ServerWebServersRead, "Read web server configuration", "View discovered web servers, their runtime status, and configuration metadata on the server.", "server_management"),
        new(ServerWebServersManage, "Manage web server configuration", "Integrate, reload, and test web server configuration on the server.", "server_management"),
        new(ServerGitRead, "Read Git repositories", "View registered Git repositories, their status, branches, history, and diffs on the server.", "server_files"),
        new(ServerGitManage, "Manage Git repositories", "Commit, pull, push, switch, create and delete branches, and revert commits in registered Git repositories on the server.", "server_files"),
        new(ServerTunnelsRead, "Read tunnel configuration", "View safe tunnel, FRP server profile, runtime, and sanitized log state on the server.", "server_network"),
        new(ServerTunnelsManage, "Manage tunnels", "Create, change, apply, stop, and delete server tunnel configuration and FRP runtime settings.", "server_network"),
        new(ServerRegistryRead, "Read configuration registry", "Browse supported RelaxKonOS configuration values and their synchronization state.", "server_management"),
        new(ServerRegistryWrite, "Modify configuration registry", "Change supported RelaxKonOS configuration values through the registry workflow.", "server_management"),
        new(ServerProxyRead, "Read proxy status", "View safe proxy runtime, health, and sanitized diagnostics.", "server_network"),
        new(ServerProxyManage, "Manage proxy", "Manage proxy lifecycle and safe engine configuration.", "server_network"),
        new(ServerProxyProfileRead, "Read proxy profiles", "View proxy profile metadata without configuration content.", "server_network"),
        new(ServerProxyProfileManage, "Manage proxy profiles", "Create and change proxy profile metadata and configuration.", "server_network"),
        new(ServerProxyConnectionRead, "Read proxy connections", "View active proxy connection metadata.", "server_network"),
        new(ServerProxyConnectionClose, "Close proxy connections", "Close individual proxy connections.", "server_network"),
        new(ServerProxyTunRead, "Read proxy TUN state", "View proxy TUN and management-route protection state.", "server_network"),
        new(ServerProxyTunManage, "Manage proxy TUN", "Enable or disable host-wide proxy TUN networking.", "server_network"),
        new(ServerProxyRuntimeRead, "Read proxy runtime", "View managed or external proxy runtime status.", "server_network"),
        new(ServerProxyRuntimeManage, "Manage proxy runtime", "Install, update, roll back, or remove a verified proxy runtime.", "server_network"),
        new(ServerProxyRecoveryExecute, "Recover proxy networking", "Run the emergency proxy network recovery operation.", "server_network"),
    ];

    public static AppPermissionDefinition? Find(string? permissionId) =>
        All.FirstOrDefault(permission => string.Equals(permission.Id, permissionId, StringComparison.Ordinal));

    public static bool IsKnown(string? permissionId) => Find(permissionId) is not null;

    /// <summary>Returns a stable category identifier for client-side localization.</summary>
    public static string GetCategory(AppPermissionDefinition permission) => permission.Category;
}

/// <summary>English fallback metadata for a host capability requested by an application.</summary>
public sealed record AppPermissionDefinition(string Id, string DisplayName, string Description, string Category = "other");
