using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Server.HostMode;

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

    // Account mutations are serialized per authenticated user, across repository service instances.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> CredentialLocks = new();

    public async Task<IReadOnlyList<GitCredentialAccountDto>> ListCredentialAccountsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await CredentialSettings(db, userId).ToListAsync(cancellationToken);
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
        var gate = CredentialLocks.GetOrAdd(userId, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var key = CredentialKey(uri);
            var setting = await CredentialSettings(db, userId).SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
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
            if (setting is null)
            {
                db.Add(new AppSetting { UserId = userId, Scope = AppSettingsScope.User, ScopeId = userId,
                    AppId = CredentialAppId, Key = key, ValueJson = value, SchemaVersion = 2,
                    Revision = 1, UpdatedAt = DateTimeOffset.UtcNow });
            }
            else { setting.ValueJson = value; setting.SchemaVersion = 2; setting.Revision++; setting.UpdatedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(host, account);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteCredentialAccountAsync(Guid userId, string accountId, CancellationToken cancellationToken = default)
    {
        var gate = CredentialLocks.GetOrAdd(userId, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var settings = await CredentialSettings(db, userId).ToListAsync(cancellationToken);
            foreach (var setting in settings)
            {
                var stored = ReadHost(setting);
                if (stored is null || stored.Accounts.RemoveAll(x => ToDto(stored.Host, x).Id == accountId) == 0) continue;
                if (stored.Accounts.Count == 0) db.Remove(setting);
                else
                {
                    if (!stored.Accounts.Any(x => x.IsDefault)) stored.Accounts[0] = stored.Accounts[0] with { IsDefault = true };
                    setting.ValueJson = JsonSerializer.Serialize(stored); setting.Revision++; setting.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            return false;
        }
        finally { gate.Release(); }
    }

    private static IQueryable<AppSetting> CredentialSettings(RelaxKonOSDbContext db, Guid userId) => db.Set<AppSetting>().Where(item =>
        item.UserId == userId && item.Scope == AppSettingsScope.User && item.ScopeId == userId && item.AppId == CredentialAppId && item.Key.StartsWith("accounts-"));

    private static StoredGitHostCredentials? ReadHost(AppSetting setting) => JsonSerializer.Deserialize<StoredGitHostCredentials>(setting.ValueJson);

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
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = CredentialKey(remoteUri);
        var setting = await CredentialSettings(db, userId).SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
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

    private sealed record StoredGitCredential(string Username, string ProtectedPassword, bool IsDefault);
    private sealed record StoredGitHostCredentials(string Host, List<StoredGitCredential> Accounts);
}
