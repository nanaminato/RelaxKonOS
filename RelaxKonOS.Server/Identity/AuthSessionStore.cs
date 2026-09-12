using System.Collections.Concurrent;

namespace RelaxKonOS.Server.Identity;

/// <summary>Server 端会话/刷新令牌吊销簿。单例，线程安全。
/// 持有 refreshToken → 会话记录的映射，支持 refresh 时验旧 token、logout 时吊销。当前仅内存，重启丢失。</summary>
public sealed class AuthSessionStore
{
    private readonly ConcurrentDictionary<string, RefreshRecord> _refresh = new();

    /// <summary>注册一个新的刷新令牌。</summary>
    public void Register(Guid sessionId, string refreshToken, Guid userId, Guid workspaceId, Guid deviceId,
        DateTimeOffset expiresAt, DateTimeOffset absoluteExpiresAt, string authenticationMethod, DateTimeOffset authenticatedAt, long securityVersion)
    {
        _refresh[refreshToken] = new RefreshRecord(sessionId, userId, workspaceId, deviceId, expiresAt, absoluteExpiresAt, authenticationMethod, authenticatedAt, securityVersion);
    }

    /// <summary>
    /// Atomically consumes a one-time refresh token. A token can therefore produce at most one
    /// successor even when two requests race at the server boundary.
    /// </summary>
    public bool TryConsume(string refreshToken, out RefreshRecord record)
    {
        if (!_refresh.TryRemove(refreshToken, out record!))
            return false;

        if (record.ExpiresAt > DateTimeOffset.UtcNow)
            return true;

        record = default!;
        return false;
    }

    public event Action<Guid>? UserRevoked;
    public void RevokeUser(Guid userId)
    {
        foreach (var entry in _refresh)
            if (entry.Value.UserId == userId) _refresh.TryRemove(entry.Key, out _);
        UserRevoked?.Invoke(userId);
    }
    /// <summary>吊销刷新令牌（登出或刷新后旧 token 作废）。</summary>
    public bool Revoke(string refreshToken)
        => _refresh.TryRemove(refreshToken, out _);

    /// <summary>Removes expired records so a long-running server does not retain stale sessions.</summary>
    public int RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var pair in _refresh)
        {
            if (pair.Value.ExpiresAt <= now && _refresh.TryRemove(pair.Key, out _))
                removed++;
        }
        return removed;
    }
}

/// <summary>刷新令牌对应的会话记录。</summary>
public sealed record RefreshRecord(
    Guid SessionId,
    Guid UserId,
    Guid WorkspaceId,
    Guid DeviceId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    string AuthenticationMethod, DateTimeOffset AuthenticatedAt, long SecurityVersion);
