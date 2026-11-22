using System.Net;

namespace RelaxKonOS.Server.Docker;

public static class DockerProxyAddress
{
    /// <summary>
    /// True when a proxy URL points at the local machine. A build step runs inside a container,
    /// where loopback is that container rather than the host, so such an address can be stored and
    /// handed to a build command but can never be reached by one. Measured on Windows with Docker
    /// Desktop: neither <c>127.0.0.1</c> nor <c>host.docker.internal</c> reaches a host listener
    /// from a BuildKit sandbox. See docs/applications/RelaxKonOS.DockerManager.md §3.5.
    /// </summary>
    public static bool IsLoopbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) return false;
        // Uri keeps an IPv6 host in brackets, and IPAddress.IsLoopback covers both 127.0.0.0/8 and ::1.
        var host = uri.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
