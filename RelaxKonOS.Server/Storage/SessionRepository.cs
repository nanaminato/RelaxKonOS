using System.Collections.Concurrent;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

/// <summary>Session 仓储。每次登录新建 Session。</summary>
public interface ISessionRepository
{
    Session? FindById(Guid id);
    Session Add(Session session);
    void Update(Session session);
}

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly ConcurrentDictionary<Guid, Session> _byId = new();

    public Session? FindById(Guid id) => _byId.TryGetValue(id, out var s) ? s : null;
    public Session Add(Session s) { _byId[s.Id] = s; return s; }
    public void Update(Session s) => _byId[s.Id] = s;
}
