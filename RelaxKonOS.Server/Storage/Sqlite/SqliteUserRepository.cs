using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

/// <summary>User 仓储的 EF Core + SQLite 实现。Scoped（依赖 Scoped DbContext）。对应 InMemoryUserRepository。</summary>
public sealed class SqliteUserRepository : IUserRepository
{
    private readonly RelaxKonOSDbContext _db;

    public SqliteUserRepository(RelaxKonOSDbContext db) => _db = db;

    public User? FindByUsername(string username, PlatformKind platform)
        => _db.Users.AsNoTracking().FirstOrDefault(u => u.Username == username && u.Platform == platform);

    public User? FindById(Guid id)
        => _db.Users.AsNoTracking().FirstOrDefault(u => u.Id == id);

    public User? FindByIdentity(string identity, PlatformKind platform)
        => _db.Users.AsNoTracking().SingleOrDefault(u => u.PlatformIdentity == identity && u.Platform == platform);
    public void Update(User user)
    {
        var tracked = _db.Users.Local.FirstOrDefault(u => u.Id == user.Id);
        if (tracked is not null) _db.Entry(tracked).State = EntityState.Detached;
        _db.Users.Update(user);
        _db.SaveChanges();
    }
    public User Add(User user)
    {
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    public void UpdateLastLogin(Guid id, DateTimeOffset at)
    {
        // tracking 查询：加载后修改属性，SaveChanges 检测变更
        var u = _db.Users.Find(id);
        if (u is null) return;
        u.LastLoginAt = at;
        _db.SaveChanges();
    }
}
