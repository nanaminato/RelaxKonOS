using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>
/// Resolves Git credentials by remote host instead of by hosting provider. This keeps transport
/// authentication independent from GitHub/GitLab/Gitee web integrations and also supports
/// self-hosted Git services.
/// </summary>
internal sealed class GitCredentialResolver
{
    private readonly Dictionary<string, List<GitCredentialAccount>> _accounts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GitCredentialAccount> Accounts => _accounts.Values.SelectMany(static x => x).ToArray();

    public void AddOrUpdate(GitCredentialAccount account)
    {
        var host = NormalizeHost(account.Host);
        if (host.Length == 0) throw new ArgumentException("A Git credential account requires a host.", nameof(account));

        if (!_accounts.TryGetValue(host, out var accounts))
        {
            accounts = [];
            _accounts.Add(host, accounts);
        }

        var index = accounts.FindIndex(x => string.Equals(x.Username, account.Username, StringComparison.OrdinalIgnoreCase));
        var normalized = account with { Host = host };
        if (index >= 0) accounts[index] = normalized;
        else accounts.Add(normalized);
    }

    public bool Remove(string host, string username)
    {
        host = NormalizeHost(host);
        if (!_accounts.TryGetValue(host, out var accounts)) return false;
        var removed = accounts.RemoveAll(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)) > 0;
        if (accounts.Count == 0) _accounts.Remove(host);
        return removed;
    }

    public GitCredentialAccount? Resolve(string remoteUrl, string? preferredUsername = null)
    {
        var host = TryGetHost(remoteUrl);
        if (host is null || !_accounts.TryGetValue(host, out var accounts) || accounts.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredUsername))
        {
            var preferred = accounts.FirstOrDefault(x => string.Equals(x.Username, preferredUsername, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return preferred;
        }

        return accounts.FirstOrDefault(x => x.IsDefault) ?? accounts[0];
    }

    public static string? TryGetHost(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        var value = remoteUrl.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return NormalizeHost(uri.Host);

        // SCP-style SSH remote: git@gitee.com:owner/repository.git
        var at = value.IndexOf('@');
        var colon = value.IndexOf(':', at + 1);
        if (at >= 0 && colon > at + 1)
            return NormalizeHost(value[(at + 1)..colon]);

        return null;
    }

    private static string NormalizeHost(string? host) => (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
}

/// <summary>A saved HTTPS identity for one Git host. Secret persistence remains owned by the server credential store.</summary>
internal sealed record GitCredentialAccount(
    string Host,
    string Username,
    string Secret,
    bool IsDefault = false,
    string? Provider = null)
{
    public GitCredentialRequest ToRequest(bool saveCredentials = true) => new(Username, Secret, saveCredentials);
}
