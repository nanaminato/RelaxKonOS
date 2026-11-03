using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.Git;

public sealed partial class LocalGitRepositoryService
{
    private async Task<Uri?> GetFetchRemoteUriAsync(string gitPath, string repoPath,
        string? remote, string? branch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            branch ??= await GetCurrentBranchAsync(gitPath, repoPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(branch))
                remote = await GetBranchConfigAsync(gitPath, repoPath, branch, "remote", cancellationToken);
            if (string.IsNullOrWhiteSpace(remote))
            {
                // Git uses the sole configured remote when no branch remote is configured;
                // otherwise its default is origin. pushRemote/pushurl do not select fetch credentials.
                var remotes = await RunGitAsync(gitPath, repoPath, ["remote"], cancellationToken);
                var names = remotes.Success ? remotes.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : [];
                remote = names.Length == 1 ? names[0] : "origin";
            }
        }
        if (remote == "." || remote.StartsWith('-')) return null;
        // Git resolves both remote names and explicit URLs, including insteadOf rewrites. This
        // metadata-only command never contacts a server and never selects remote.pushurl.
        var resolved = await RunGitAsync(gitPath, repoPath, ["ls-remote", "--get-url", remote], cancellationToken);
        if (!resolved.Success || !Uri.TryCreate(resolved.Output.Trim(), UriKind.Absolute, out var uri)) return null;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? uri : null;
    }


    public async Task<IReadOnlyList<GitCredentialAccountDto>> ListCredentialAccountsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await storage.CredentialsAsync(userId, cancellationToken);
        return settings.Select(ReadHost).OfType<StoredGitHostCredentials>().SelectMany(stored =>
            stored.Accounts.Select(account => ToDto(stored.Host, account))).OrderBy(x => x.Host, StringComparer.Ordinal)
            .ThenBy(x => x.Username, StringComparer.Ordinal).ToArray();
    }

    public async Task<GitCredentialAccountDto> SaveCredentialAccountAsync(Guid userId, GitCredentialAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        var uri = ParseCredentialHost(request.Host);
        var host = uri.Authority.ToLowerInvariant();
        var username = request.Username?.Trim() ?? "";
        if (username.Length == 0 || username.Length > 256 || username.Any(char.IsControl))
            throw new ArgumentException("A valid username is required.");
        if (request.Password is not null && (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 16384 || request.Password.Any(char.IsControl)))
            throw new ArgumentException("A valid access token is required.");
        var key = CredentialKey(uri);
        await using var edit = await storage.BeginCredentialUpdateAsync(userId, key, cancellationToken);
        {
            var setting = edit.Settings.SingleOrDefault(x => x.Key == key);
            var stored = setting is null ? null : ReadHost(setting);
            var accounts = stored?.Accounts ?? [];
            var existing = accounts.FindIndex(x => x.Username == username);
            if (existing < 0 && request.Password is null) throw new ArgumentException("An access token is required for a new account.");
            var secret = request.Password is null ? accounts[existing].ProtectedPassword
                : AccountProtector(userId, host, username).Protect(request.Password);
            var isDefault = request.IsDefault || accounts.Count == 0 || (existing >= 0 && accounts[existing].IsDefault);
            if (isDefault)
                for (var i = 0; i < accounts.Count; i++) accounts[i] = accounts[i] with { IsDefault = false };
            var account = new StoredGitCredential(username, secret, isDefault);
            if (existing < 0) accounts.Add(account); else accounts[existing] = account;
            var value = JsonSerializer.Serialize(new StoredGitHostCredentials(host, accounts));
            await edit.SaveAsync(key, value, cancellationToken);
            return ToDto(host, account);
        }
    }

    public async Task<bool> DeleteCredentialAccountAsync(Guid userId, string accountId, CancellationToken cancellationToken = default)
    {
        await using var edit = await storage.BeginCredentialUpdateAsync(userId, null, cancellationToken);
        {
            var settings = edit.Settings;
            foreach (var setting in settings)
            {
                var stored = ReadHost(setting);
                if (stored is null || stored.Accounts.RemoveAll(x => ToDto(stored.Host, x).Id == accountId) == 0) continue;
                if (stored.Accounts.Count == 0) await edit.SaveAsync(setting.Key, null, cancellationToken);
                else
                {
                    if (!stored.Accounts.Any(x => x.IsDefault)) stored.Accounts[0] = stored.Accounts[0] with { IsDefault = true };
                    await edit.SaveAsync(setting.Key, JsonSerializer.Serialize(stored), cancellationToken);
                }
                return true;
            }
            return false;
        }
    }

    private static StoredGitHostCredentials? ReadHost(GitCredentialSetting setting) => JsonSerializer.Deserialize<StoredGitHostCredentials>(setting.ValueJson);

    private IDataProtector AccountProtector(Guid userId, string host, string username) =>
        _credentialProtector.CreateProtector(userId.ToString("N"), host, username);

    private static GitCredentialAccountDto ToDto(string host, StoredGitCredential account) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host + "\n" + account.Username))).ToLowerInvariant(),
        host, account.Username, account.IsDefault);

    private static Uri ParseCredentialHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 512 || host.Any(char.IsWhiteSpace) || host.Contains('@') || host.Contains('?') || host.Contains('#') || host.Contains('\\'))
            throw new ArgumentException("A valid HTTPS host is required.");
        var value = host.Contains("://", StringComparison.Ordinal) ? host : "https://" + host;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Use an HTTPS host with an optional port, without a repository path.");
        return new UriBuilder(uri) { Host = uri.IdnHost.TrimEnd('.'), UserName = "", Password = "" }.Uri;
    }

    private async Task<GitCredentialRequest?> GetStoredCredentialsAsync(Guid userId, Uri remoteUri, CancellationToken cancellationToken)
    {
        if (remoteUri.Scheme != Uri.UriSchemeHttps) return null;
        var key = CredentialKey(remoteUri);
        var setting = await storage.FindCredentialAsync(userId, key, cancellationToken);
        if (setting is null) return null;
        try
        {
            var stored = ReadHost(setting);
            var host = ParseCredentialHost(remoteUri.Authority).Authority.ToLowerInvariant();
            if (stored?.Host != host) return null;
            var username = Uri.UnescapeDataString(remoteUri.UserInfo.Split(':')[0]);
            var account = username.Length > 0 ? stored?.Accounts.FirstOrDefault(x => x.Username == username)
                : stored?.Accounts.FirstOrDefault(x => x.IsDefault);
            return account is null ? null : new(account.Username, AccountProtector(userId, host, account.Username).Unprotect(account.ProtectedPassword), false);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            logger.LogWarning("Could not load a stored Git credential for the current user.");
            return null;
        }
    }

    private Task<GitCredentialAccountDto> SaveCredentialsAsync(Guid userId, Uri remoteUri, GitCredentialRequest credentials, CancellationToken cancellationToken)
        => SaveCredentialAccountAsync(userId, new(remoteUri.Authority, credentials.Username, credentials.Password), cancellationToken);

    private static string CredentialKey(Uri remoteUri)
    {
        var identity = ParseCredentialHost(remoteUri.Authority).Authority.ToLowerInvariant();
        return "accounts-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

}
