using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>Helper-owned closed package/service mapping. HTTP and IPC cannot select package names.</summary>
public sealed record HostPackageDefinition(string ComponentId, string PackageName, string Executable, string Service,
    IReadOnlyList<string> ExtraPackages);
public static class HostPackageDefinitions
{
    public static HostPackageDefinition? Find(string? componentId) => componentId switch
    {
        ComponentIds.SingBox => new(ComponentIds.SingBox, "sing-box", "/usr/bin/sing-box", "sing-box.service", []),
        ComponentIds.Mihomo => new(ComponentIds.Mihomo, "mihomo", "/usr/bin/mihomo", "mihomo.service", []),
        ComponentIds.Git => new(ComponentIds.Git, "git", "/usr/bin/git", "", []),
        ComponentIds.Tailscale => new(ComponentIds.Tailscale, "tailscale", "/usr/bin/tailscale", "tailscaled.service", []),
        ComponentIds.Nginx => new(ComponentIds.Nginx, "nginx", "/usr/sbin/nginx", "nginx.service", ["acl"]),
        ComponentIds.Squid => new(ComponentIds.Squid, "squid", "/usr/sbin/squid", "squid.service", []),
        ComponentIds.Samba => new(ComponentIds.Samba, "samba", "/usr/sbin/smbd", "smbd.service", ["acl"]),
        ComponentIds.Docker => new(ComponentIds.Docker, "docker-ce", "/usr/bin/dockerd", "docker.service",
            ["docker-ce-cli", "containerd.io", "docker-buildx-plugin", "docker-compose-plugin"]),
        _ => null
    };
    public static bool SupportsVersion(string componentId, string version)
    {
        if (componentId != ComponentIds.SingBox) return true;
        var match = System.Text.RegularExpressions.Regex.Match(version,
            @"^(?:[0-9]+:)?(?<upstream>[0-9]+\.[0-9]+\.[0-9]+)(?:-[0-9][0-9A-Za-z.+~]*)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && SingBoxRuntimeSupport.CapabilityFor(match.Groups["upstream"].Value) is not null;
    }
    public static bool KnownPackage(string? package) => package is "sing-box" or "mihomo" or "nginx" or "squid" or "samba" or "docker-ce" or "git" or "tailscale";
}
