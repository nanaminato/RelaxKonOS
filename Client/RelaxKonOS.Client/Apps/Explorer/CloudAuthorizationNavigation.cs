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
        if (protocol != "baidu-netdisk" || candidate.IdnHost is not ("openapi.baidu.com" or "open.baidu.com")) return false;
        uri = candidate;
        return true;
    }
}
