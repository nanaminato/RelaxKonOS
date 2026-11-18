using System.IO.Enumeration;
using System.Net;

namespace RelaxKonOS.Server.Docker;

/// <summary>Uses saved NO_PROXY host patterns, never treating operator input as a regular expression.</summary>
internal sealed class OutboundDownloadProxy : IWebProxy, ICredentials
{
    private readonly WebProxy _http;
    private readonly WebProxy _https;
    private readonly string[] _bypass;

    internal OutboundDownloadProxy(DockerProxyResolution resolution)
    {
        _http = CreateProxy(resolution.HttpProxy);
        _https = CreateProxy(resolution.HttpsProxy);
        _bypass = resolution.NoProxy.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Credentials = this;
    }

    public ICredentials? Credentials { get; set; }
    public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? destination : Select(destination).Address;
    public bool IsBypassed(Uri host) => _bypass.Any(entry => Matches(entry, host));
    internal WebProxy Select(Uri destination) => destination.Scheme == Uri.UriSchemeHttps ? _https : _http;
    NetworkCredential? ICredentials.GetCredential(Uri uri, string authType) =>
        (uri == _https.Address ? _https : _http).Credentials?.GetCredential(uri, authType);

    private static WebProxy CreateProxy(string endpoint)
    {
        var uri = new Uri(endpoint, UriKind.Absolute);
        var address = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        var proxy = new WebProxy(address);
        if (uri.UserInfo.Length > 0)
        {
            var credentials = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(credentials[0]),
                credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "");
        }
        return proxy;
    }

    private static bool Matches(string entry, Uri destination)
    {
        var host = destination.IdnHost.TrimEnd('.').Trim('[', ']');
        if (entry == "*") return true;
        if (IPNetwork.TryParse(entry, out var network))
            return IPAddress.TryParse(host, out var address) && network.Contains(address);
        var pattern = entry;
        // Bracketed IPv6 may have a port; unbracketed IPv6 is always an address.
        var colon = entry.LastIndexOf(':');
        if (colon > 0 && (entry.IndexOf(':') == colon || entry[..colon].EndsWith(']')))
        {
            if (!int.TryParse(entry[(colon + 1)..], out var port) || port != destination.Port) return false;
            pattern = entry[..colon];
        }
        pattern = pattern.Trim('[', ']').TrimEnd('.');
        if (pattern.Contains('*')) return FileSystemName.MatchesSimpleExpression(pattern, host, ignoreCase: true);
        pattern = pattern.TrimStart('.');
        return host.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            || !IPAddress.TryParse(pattern, out _) && host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
    }
}
