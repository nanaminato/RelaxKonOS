using System.Collections.Concurrent;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

public interface IAppSettingsRepository
{
    AppSetting? Find(Guid userId, AppSettingsScope scope, Guid scopeId, string appId, string key);
    AppSettingsWriteResult Upsert(AppSetting setting, long? expectedRevision);
    int DeleteForApp(Guid userId, string appId);
}

public sealed record AppSettingsWriteResult(AppSetting? Setting, bool IsConflict)
{
    public static AppSettingsWriteResult Conflict { get; } = new(null, true);
}

/// <summary>Development-only in-memory implementation. Mirrors the SQLite optimistic-concurrency semantics.</summary>
public sealed class InMemoryAppSettingsRepository : IAppSettingsRepository
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<(Guid UserId, AppSettingsScope Scope, Guid ScopeId, string AppId, string Key), AppSetting> _items = new();

    public AppSetting? Find(Guid userId, AppSettingsScope scope, Guid scopeId, string appId, string key)
    {
        lock (_gate)
            return _items.TryGetValue((userId, scope, scopeId, appId, key), out var value) ? Copy(value) : null;
    }

    public AppSettingsWriteResult Upsert(AppSetting setting, long? expectedRevision)
    {
        var tuple = (setting.UserId, setting.Scope, setting.ScopeId, setting.AppId, setting.Key);
        lock (_gate)
        {
            if (_items.TryGetValue(tuple, out var existing))
            {
                if (expectedRevision is { } expected && expected != existing.Revision)
                    return AppSettingsWriteResult.Conflict;
                setting.Revision = existing.Revision + 1;
            }
            else
            {
                if (expectedRevision is { } expected && expected != 0)
                    return AppSettingsWriteResult.Conflict;
                setting.Revision = 1;
            }

            setting.UpdatedAt = DateTimeOffset.UtcNow;
            var saved = Copy(setting);
            _items[tuple] = saved;
            return new AppSettingsWriteResult(Copy(saved), false);
        }
    }

    public int DeleteForApp(Guid userId, string appId)
    {
        lock (_gate)
        {
            var keys = _items.Keys.Where(key => key.UserId == userId
                && key.AppId.Equals(appId, StringComparison.Ordinal)).ToArray();
            foreach (var key in keys)
                _items.TryRemove(key, out _);
            return keys.Length;
        }
    }

    private static AppSetting Copy(AppSetting value) => new()
    {
        UserId = value.UserId, Scope = value.Scope, ScopeId = value.ScopeId,
        AppId = value.AppId, Key = value.Key, ValueJson = value.ValueJson,
        SchemaVersion = value.SchemaVersion, Revision = value.Revision, UpdatedAt = value.UpdatedAt,
    };
}
