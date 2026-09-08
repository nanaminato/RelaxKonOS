using System.Collections.Concurrent;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

/// <summary>Device 仓储。按 (name, platform) 与 Id 索引。同一设备重复登录复用记录。</summary>
public interface IDeviceRepository
{
    Device? FindByNameAndPlatform(string name, string platform);
    Device? FindById(Guid id);
    Device Add(Device device);
    void Update(Device device);
}

public sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly ConcurrentDictionary<Guid, Device> _byId = new();
    private readonly ConcurrentDictionary<(string name, string platform), Guid> _byKey = new();

    public Device? FindByNameAndPlatform(string name, string platform)
        => _byKey.TryGetValue((name, platform), out var id) && _byId.TryGetValue(id, out var d) ? d : null;

    public Device? FindById(Guid id) => _byId.TryGetValue(id, out var d) ? d : null;

    public Device Add(Device d)
    {
        _byId[d.Id] = d;
        _byKey[(d.Name, d.Platform)] = d.Id;
        return d;
    }

    public void Update(Device d) => _byId[d.Id] = d;
}
