using System.Collections.Concurrent;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

/// <summary>用户仓储。按 (username, platform) 与 Id 索引。</summary>
public interface IUserRepository
{
    User? FindByUsername(string username, PlatformKind platform);
    User? FindById(Guid id);
    User Add(User user);
    void UpdateLastLogin(Guid id, DateTimeOffset at);
}

/// <summary>内存实现。Singleton，ConcurrentDictionary。内存实现，重启丢失。</summary>
public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<Guid, User> _byId = new();
    private readonly ConcurrentDictionary<(string username, PlatformKind platform), Guid> _byName = new();

    public User? FindByUsername(string username, PlatformKind platform)
        => _byName.TryGetValue((username, platform), out var id) && _byId.TryGetValue(id, out var u) ? u : null;

    public User? FindById(Guid id) => _byId.TryGetValue(id, out var u) ? u : null;

    public User Add(User user)
    {
        _byId[user.Id] = user;
        _byName[(user.Username, user.Platform)] = user.Id;
        return user;
    }

    public void UpdateLastLogin(Guid id, DateTimeOffset at)
    {
        if (_byId.TryGetValue(id, out var u))
            u.LastLoginAt = at;
    }
}
