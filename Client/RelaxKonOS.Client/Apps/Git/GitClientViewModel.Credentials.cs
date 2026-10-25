using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

public sealed partial class GitClientViewModel
{
    private readonly GitCredentialResolver _credentialResolver = new();

    /// <summary>
    /// Resolves a previously entered HTTPS credential for a remote. Authentication is keyed by the
    /// actual remote host, not by GitHub/GitLab/Gitee branding, so the same path works for self-hosted
    /// Git services as well.
    /// </summary>
    private GitCredentialRequest? ResolveCredentialForRemote(string? remoteName, bool saveCredentials = true)
    {
        var remote = FindRemote(remoteName);
        var url = remote?.PushUrl ?? remote?.FetchUrl;
        return url is null ? null : _credentialResolver.Resolve(url)?.ToRequest(saveCredentials);
    }

    /// <summary>
    /// Remembers a credential entered for a remote for the lifetime of this Project Editor window.
    /// Durable storage remains server-owned; when SaveCredentials is true the existing Git endpoint
    /// persists the same credential in its protected per-user credential store.
    /// </summary>
    private void RememberCredentialForRemote(string? remoteName, GitCredentialRequest credentials)
    {
        var remote = FindRemote(remoteName);
        var url = remote?.PushUrl ?? remote?.FetchUrl;
        var host = GitCredentialResolver.TryGetHost(url);
        if (host is null) return;

        _credentialResolver.AddOrUpdate(new GitCredentialAccount(
            host,
            credentials.Username,
            credentials.Password,
            IsDefault: true,
            Provider: GitHostingLinks.Resolve(url)?.Provider));
    }

    private GitRemoteDto? FindRemote(string? remoteName)
    {
        if (string.IsNullOrWhiteSpace(remoteName)) return null;
        return Remotes.FirstOrDefault(remote =>
            string.Equals(remote.Name, remoteName, StringComparison.Ordinal));
    }
}
