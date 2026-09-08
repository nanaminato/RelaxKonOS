using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.ImageMirrors;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

public sealed class SqliteImageMirrorRepository(RemoteOsDbContext db) : IImageMirrorRepository
{
    public IReadOnlyList<ImageMirror> List(Guid userId, ImageMirrorTarget target) => db.ImageMirrors.AsNoTracking()
        .Where(x => x.UserId == userId && x.Target == target).ToArray()
        // Microsoft.EntityFrameworkCore.Sqlite cannot translate DateTimeOffset ORDER BY.
        // Mirror lists are small user preferences, so ordering after the filtered query is safe.
        .OrderBy(x => x.CreatedAt).ToArray();

    public ImageMirror? Find(Guid userId, ImageMirrorTarget target, Guid id) => db.ImageMirrors.AsNoTracking()
        .FirstOrDefault(x => x.UserId == userId && x.Target == target && x.Id == id);

    public ImageMirror Create(ImageMirror mirror)
    {
        mirror.Id = Guid.NewGuid();
        mirror.CreatedAt = mirror.UpdatedAt = DateTimeOffset.UtcNow;
        db.ImageMirrors.Add(mirror);
        db.SaveChanges();
        return mirror;
    }

    public ImageMirror? Update(ImageMirror mirror)
    {
        var current = db.ImageMirrors.FirstOrDefault(x => x.Id == mirror.Id && x.UserId == mirror.UserId && x.Target == mirror.Target);
        if (current is null) return null;
        current.Name = mirror.Name;
        current.Endpoint = mirror.Endpoint;
        current.UpdatedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        return current;
    }

    public bool Delete(Guid userId, ImageMirrorTarget target, Guid id)
    {
        var current = db.ImageMirrors.FirstOrDefault(x => x.Id == id && x.UserId == userId && x.Target == target);
        if (current is null) return false;
        db.ImageMirrors.Remove(current);
        db.SaveChanges();
        return true;
    }

    public bool Select(Guid userId, ImageMirrorTarget target, Guid? id)
    {
        var mirrors = db.ImageMirrors.Where(x => x.UserId == userId && x.Target == target).ToArray();
        if (id is { } selectedId && !mirrors.Any(x => x.Id == selectedId)) return false;
        foreach (var mirror in mirrors) mirror.IsSelected = mirror.Id == id;
        db.SaveChanges();
        return true;
    }

    public ImageMirror? GetSelected(Guid userId, ImageMirrorTarget target) => db.ImageMirrors.AsNoTracking()
        .FirstOrDefault(x => x.UserId == userId && x.Target == target && x.IsSelected);
}
