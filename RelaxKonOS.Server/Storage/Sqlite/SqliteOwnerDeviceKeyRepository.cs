using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

public sealed class SqliteOwnerDeviceKeyRepository(RelaxKonOSDbContext db) : IOwnerDeviceKeyRepository
{
    public OwnerDeviceKey? FindActive(Guid id)
        => db.OwnerDeviceKeys.FirstOrDefault(key => key.Id == id && key.RevokedAt == null);

    public IReadOnlyList<OwnerDeviceKey> ListActive(Guid userId)
        => db.OwnerDeviceKeys.AsNoTracking().Where(key => key.UserId == userId && key.RevokedAt == null)
            .OrderBy(key => key.CreatedAt).ToArray();

    public OwnerDeviceKey Add(OwnerDeviceKey key)
    {
        db.OwnerDeviceKeys.Add(key);
        db.SaveChanges();
        return key;
    }

    public void Update(OwnerDeviceKey key)
    {
        db.OwnerDeviceKeys.Update(key);
        db.SaveChanges();
    }
}
