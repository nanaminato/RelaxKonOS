using System.Collections.Concurrent;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

/// <summary>Device storage. Id is the identity; name and platform are display/login lookup fields.</summary>
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
    public Device? FindByNameAndPlatform(string name, string platform)
        => _byId.Values.Where(d => d.Name == name && d.Platform == platform)
            .OrderBy(d => d.Id).FirstOrDefault();

    public Device? FindById(Guid id) => _byId.TryGetValue(id, out var d) ? d : null;

    public Device Add(Device d)
    {
        _byId[d.Id] = d;
        return d;
    }

    public void Update(Device d) => _byId[d.Id] = d;
}
