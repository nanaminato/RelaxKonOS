using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>Helper-owned closed package/service mapping. HTTP and IPC cannot select package names.</summary>
public sealed record HostPackageDefinition(string ComponentId, string PackageName, string Executable, string Service,
    IReadOnlyList<string> ExtraPackages);
public static class HostPackageDefinitions
{
    public static HostPackageDefinition? Find(string? componentId) => componentId switch
    {
        ComponentIds.Git => new(ComponentIds.Git, "git", "/usr/bin/git", "", []),
        ComponentIds.Tailscale => new(ComponentIds.Tailscale, "tailscale", "/usr/bin/tailscale", "tailscaled.service", []),
        ComponentIds.Nginx => new(ComponentIds.Nginx, "nginx", "/usr/sbin/nginx", "nginx.service", ["acl"]),
        ComponentIds.Squid => new(ComponentIds.Squid, "squid", "/usr/sbin/squid", "squid.service", []),
        ComponentIds.Samba => new(ComponentIds.Samba, "samba", "/usr/sbin/smbd", "smbd.service", ["acl"]),
        ComponentIds.Docker => new(ComponentIds.Docker, "docker-ce", "/usr/bin/dockerd", "docker.service",
            ["docker-ce-cli", "containerd.io", "docker-buildx-plugin", "docker-compose-plugin"]),
        _ => null
    };
    public static bool KnownPackage(string? package) => package is "nginx" or "squid" or "samba" or "docker-ce" or "git" or "tailscale";
}
