using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.Browser;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

/// <summary>Workspace 仓储的 EF Core + SQLite 实现。Workspace 配置由注册表持有，不映射到本实体。</summary>
public sealed class SqliteWorkspaceRepository : IWorkspaceRepository
{
    private readonly RemoteOsDbContext _db;

    public SqliteWorkspaceRepository(RemoteOsDbContext db) => _db = db;

    public Workspace? FindByUserId(Guid userId)
        => Normalize(_db.Workspaces.FirstOrDefault(w => w.UserId == userId));

    public Workspace? FindById(Guid id)
        => Normalize(_db.Workspaces.FirstOrDefault(w => w.Id == id));

    public Workspace Add(Workspace workspace)
    {
        _db.Workspaces.Add(workspace);
        _db.SaveChanges();
        return workspace;
    }

    public void Update(Workspace workspace)
    {
        // Workspace reads stay tracked so EF retains the synthesized ordinal keys used
        // by the owned JSON collections. Reattaching an AsNoTracking graph loses those
        // shadow values and makes collection updates impossible to persist.
        if (_db.Entry(workspace).State == EntityState.Detached)
            throw new InvalidOperationException("Cannot update a detached workspace.");
        _db.SaveChanges();
    }

    private static Workspace? Normalize(Workspace? w) => w;
}
