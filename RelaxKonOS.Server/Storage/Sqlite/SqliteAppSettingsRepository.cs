using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

/// <summary>SQLite implementation of application-private settings with revision-based optimistic concurrency.</summary>
public sealed class SqliteAppSettingsRepository(RemoteOsDbContext db) : IAppSettingsRepository
{
    public AppSetting? Find(Guid userId, AppSettingsScope scope, Guid scopeId, string appId, string key)
        => db.AppSettings.AsNoTracking().FirstOrDefault(setting =>
            setting.UserId == userId && setting.Scope == scope && setting.ScopeId == scopeId
            && setting.AppId == appId && setting.Key == key);

    public AppSettingsWriteResult Upsert(AppSetting setting, long? expectedRevision)
    {
        var existing = db.AppSettings.FirstOrDefault(value =>
            value.UserId == setting.UserId && value.Scope == setting.Scope && value.ScopeId == setting.ScopeId
            && value.AppId == setting.AppId && value.Key == setting.Key);

        if (existing is null)
        {
            if (expectedRevision is { } expected && expected != 0)
                return AppSettingsWriteResult.Conflict;
            setting.Revision = 1;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
            db.AppSettings.Add(setting);
        }
        else
        {
            if (expectedRevision is { } expected && expected != existing.Revision)
                return AppSettingsWriteResult.Conflict;
            existing.ValueJson = setting.ValueJson;
            existing.SchemaVersion = setting.SchemaVersion;
            existing.Revision++;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            setting = existing;
        }

        try
        {
            db.SaveChanges();
            return new AppSettingsWriteResult(setting, false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppSettingsWriteResult.Conflict;
        }
        catch (DbUpdateException) when (expectedRevision is not null)
        {
            // A concurrent create violates the unique key and is equivalent to a revision conflict.
            return AppSettingsWriteResult.Conflict;
        }
    }

    public int DeleteForApp(Guid userId, string appId) => db.AppSettings
        .Where(setting => setting.UserId == userId && setting.AppId == appId)
        .ExecuteDelete();
}
