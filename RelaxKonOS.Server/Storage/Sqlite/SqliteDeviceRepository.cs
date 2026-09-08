using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

/// <summary>Device 仓储的 EF Core + SQLite 实现。Scoped。对应 InMemoryDeviceRepository。</summary>
public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private readonly RemoteOsDbContext _db;

    public SqliteDeviceRepository(RemoteOsDbContext db) => _db = db;

    public Device? FindByNameAndPlatform(string name, string platform)
        => _db.Devices.AsNoTracking().FirstOrDefault(d => d.Name == name && d.Platform == platform);

    public Device? FindById(Guid id)
        => _db.Devices.AsNoTracking().FirstOrDefault(d => d.Id == id);

    public Device Add(Device device)
    {
        _db.Devices.Add(device);
        _db.SaveChanges();
        return device;
    }

    public void Update(Device device)
    {
        _db.Devices.Update(device);
        _db.SaveChanges();
    }
}
