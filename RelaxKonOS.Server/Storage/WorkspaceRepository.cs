using System.Collections.Concurrent;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

/// <summary>Workspace 仓储。按 UserId 与 Id 索引。One User One Persistent Workspace。</summary>
public interface IWorkspaceRepository
{
    Workspace? FindByUserId(Guid userId);
    Workspace? FindById(Guid id);
    Workspace Add(Workspace workspace);
    void Update(Workspace workspace);
}

public sealed class InMemoryWorkspaceRepository : IWorkspaceRepository
{
    private readonly ConcurrentDictionary<Guid, Workspace> _byId = new();
    private readonly ConcurrentDictionary<Guid, Guid> _byUserId = new();

    public Workspace? FindByUserId(Guid userId)
        => _byUserId.TryGetValue(userId, out var id) && _byId.TryGetValue(id, out var w) ? w : null;

    public Workspace? FindById(Guid id) => _byId.TryGetValue(id, out var w) ? w : null;

    public Workspace Add(Workspace w)
    {
        _byId[w.Id] = w;
        _byUserId[w.UserId] = w.Id;
        return w;
    }

    public void Update(Workspace w) => _byId[w.Id] = w;
}
