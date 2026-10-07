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
    private async Task<GitCredentialRequest?> GetStoredCredentialsAsync(Guid userId, Uri remoteUri, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = CredentialKey(remoteUri);
        var setting = await db.Set<AppSetting>().SingleOrDefaultAsync(item =>
            item.UserId == userId && item.Scope == AppSettingsScope.User && item.ScopeId == userId &&
            item.AppId == CredentialAppId && item.Key == key, cancellationToken);
        if (setting is null) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredGitCredential>(setting.ValueJson);
            if (stored is null || string.IsNullOrWhiteSpace(stored.Username) || string.IsNullOrWhiteSpace(stored.ProtectedPassword))
                return null;
            return new GitCredentialRequest(stored.Username, _credentialProtector.Unprotect(stored.ProtectedPassword));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load a stored Git credential for the current user.");
            return null;
        }
    }

    private async Task SaveCredentialsAsync(Guid userId, Uri remoteUri, GitCredentialRequest credentials, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = CredentialKey(remoteUri);
        var setting = await db.Set<AppSetting>().SingleOrDefaultAsync(item =>
            item.UserId == userId && item.Scope == AppSettingsScope.User && item.ScopeId == userId &&
            item.AppId == CredentialAppId && item.Key == key, cancellationToken);

        var value = JsonSerializer.Serialize(new StoredGitCredential(credentials.Username, _credentialProtector.Protect(credentials.Password)));
        if (setting is null)
        {
            db.Add(new AppSetting
            {
                UserId = userId,
                Scope = AppSettingsScope.User,
                ScopeId = userId,
                AppId = CredentialAppId,
                Key = key,
                ValueJson = value,
                SchemaVersion = 1,
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            setting.ValueJson = value;
            setting.SchemaVersion = 1;
            setting.Revision++;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string CredentialKey(Uri remoteUri)
    {
        var identity = remoteUri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return "https-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private sealed record StoredGitCredential(string Username, string ProtectedPassword);

}
