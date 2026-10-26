using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Maps Git remotes to their hosting web UI without ever forwarding clone credentials.</summary>
internal static class GitHostingLinks
{
    public static Uri? Resolve(IEnumerable<GitRemoteDto> remotes, string provider, string page)
    {
        foreach (var remote in remotes)
        {
            var url = remote.FetchUrl;
            if (url.StartsWith("git@ssh.dev.azure.com:v3/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = url["git@ssh.dev.azure.com:v3/".Length..].Split('/');
                if (parts.Length == 3) url = $"https://dev.azure.com/{parts[0]}/{parts[1]}/_git/{parts[2]}";
            }
            else if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                var colon = url.IndexOf(':');
                if (colon > 4) url = "https://" + url[4..colon] + "/" + url[(colon + 1)..];
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme is not ("https" or "ssh")) continue;
            var host = source.Host;
            var actualProvider = DetectProvider(host);
            if (!string.Equals(provider, actualProvider, StringComparison.OrdinalIgnoreCase)) continue;
            var path = source.AbsolutePath.TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
            if (provider == "azure") path += page == "pulls" ? "/pullrequests" : page == "issues" ? "/commits" : "";
            else if (provider == "gitlab") path += page == "pulls" ? "/-/merge_requests" : page == "issues" ? "/-/issues" : "";
            else path += page == "pulls" ? "/pulls" : page == "issues" ? "/issues" : "";
            return new UriBuilder(Uri.UriSchemeHttps, host) { Port = source.Scheme == "https" ? source.Port : -1, Path = path, UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
        }
        return null;
    }

    public static string? DetectProvider(string host)
    {
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.StartsWith("github.", StringComparison.OrdinalIgnoreCase)) return "github";
        if (host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) || host.StartsWith("gitlab.", StringComparison.OrdinalIgnoreCase)) return "gitlab";
        if (host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase) || host.StartsWith("gitee.", StringComparison.OrdinalIgnoreCase)) return "gitee";
        if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)) return "azure";
        return null;
    }
}
