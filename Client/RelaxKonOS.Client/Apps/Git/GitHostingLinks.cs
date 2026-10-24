using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Only configured HTTPS repository pages can be opened; clone credentials are never included.</summary>
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
            var actualProvider = host == "github.com" || host.StartsWith("github.", StringComparison.OrdinalIgnoreCase) ? "github"
                : host == "gitlab.com" || host.StartsWith("gitlab.", StringComparison.OrdinalIgnoreCase) ? "gitlab"
                : host == "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) ? "azure" : null;
            if (provider != actualProvider) continue;
            var path = source.AbsolutePath.TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
            if (provider == "azure") path += page == "pulls" ? "/pullrequests" : page == "issues" ? "/commits" : "";
            else path += page == "pulls" ? provider == "gitlab" ? "/-/merge_requests" : "/pulls" : page == "issues" ? provider == "gitlab" ? "/-/issues" : "/issues" : "";
            return new UriBuilder(Uri.UriSchemeHttps, host) { Port = source.Scheme == "https" ? source.Port : -1, Path = path, UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
        }
        return null;
    }
}
