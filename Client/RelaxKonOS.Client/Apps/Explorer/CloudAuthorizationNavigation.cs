namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>Only provider-owned HTTPS pages may be launched by cloud authorization UI.</summary>
internal static class CloudAuthorizationNavigation
{
    internal static bool TryGetBrowserUri(string protocol, string? address, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || !candidate.IsDefaultPort
            || !string.IsNullOrEmpty(candidate.UserInfo)) return false;
        var accepted = protocol switch
        {
            "google-drive" => candidate.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase),
            "baidu-netdisk" => candidate.Host.Equals("openapi.baidu.com", StringComparison.OrdinalIgnoreCase)
                || candidate.Host.Equals("open.baidu.com", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!accepted) return false;
        uri = candidate;
        return true;
    }
}
