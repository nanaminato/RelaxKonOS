using System.Collections.Concurrent;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

public interface IOwnerDeviceKeyRepository
{
    OwnerDeviceKey? FindActive(Guid id);
    IReadOnlyList<OwnerDeviceKey> ListActive(Guid userId);
    OwnerDeviceKey Add(OwnerDeviceKey key);
    void Update(OwnerDeviceKey key);
}

public sealed class InMemoryOwnerDeviceKeyRepository : IOwnerDeviceKeyRepository
{
    private readonly ConcurrentDictionary<Guid, OwnerDeviceKey> _keys = new();

    public OwnerDeviceKey? FindActive(Guid id)
        => _keys.TryGetValue(id, out var key) && key.RevokedAt is null ? key : null;

    public IReadOnlyList<OwnerDeviceKey> ListActive(Guid userId)
        => _keys.Values.Where(key => key.UserId == userId && key.RevokedAt is null).ToArray();

    public OwnerDeviceKey Add(OwnerDeviceKey key)
    {
        if (!_keys.TryAdd(key.Id, key)) throw new InvalidOperationException("Owner device key already exists.");
        return key;
    }

    public void Update(OwnerDeviceKey key) => _keys[key.Id] = key;
}
