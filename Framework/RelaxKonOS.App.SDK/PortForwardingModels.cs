namespace RelaxKonOS.AppSDK;

/// <summary>Request a loopback-only SSH local forward to the connected RelaxKonOS server.</summary>
/// <remarks>The requested target must be a loopback service on the server, never an arbitrary host.</remarks>
public sealed record PortForwardRequest(
    string RemoteHost,
    int RemotePort,
    string Scheme = "http",
    int? PreferredLocalPort = null,
    string PathAndQuery = "/")
{
    public static PortForwardRequest Localhost(int remotePort, int? preferredLocalPort = null, string scheme = "http")
        => new("localhost", remotePort, scheme, preferredLocalPort);
}

/// <summary>Runtime state for one locally owned SSH process. It is intentionally not synchronized.</summary>
public sealed record PortForwardInfo(
    Guid Id,
    string RemoteHost,
    int RemotePort,
    int LocalPort,
    string Scheme,
    string PathAndQuery,
    DateTimeOffset StartedAt,
    string Status,
    string? Detail = null)
{
    public Uri LocalUri
    {
        get
        {
            var split = PathAndQuery.IndexOf('?');
            var builder = new UriBuilder(Scheme, "localhost", LocalPort)
            {
                Path = split < 0 ? PathAndQuery : PathAndQuery[..split],
                Query = split < 0 ? string.Empty : PathAndQuery[(split + 1)..],
            };
            return builder.Uri;
        }
    }
    public string Target => $"{RemoteHost}:{RemotePort}";
}

/// <summary>Non-secret SSH connection preferences persisted only on this RelaxKonOS Client host.</summary>
public sealed record PortForwardingSettings(string? SshHost = null, string? SshUser = null, int SshPort = 22)
{
    public PortForwardingSettings Normalize() => this with
    {
        SshHost = string.IsNullOrWhiteSpace(SshHost) ? null : SshHost.Trim(),
        SshUser = string.IsNullOrWhiteSpace(SshUser) ? null : SshUser.Trim(),
        SshPort = Math.Clamp(SshPort, 1, 65535),
    };
}
