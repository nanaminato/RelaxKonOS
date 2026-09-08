using System.Collections.Concurrent;
using RelaxKonOS.Protocol.ImageMirrors;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

/// <summary>Persistent image mirror settings, isolated by authenticated RelaxKonOS user.</summary>
public interface IImageMirrorRepository
{
    IReadOnlyList<ImageMirror> List(Guid userId, ImageMirrorTarget target);
    ImageMirror? Find(Guid userId, ImageMirrorTarget target, Guid id);
    ImageMirror Create(ImageMirror mirror);
    ImageMirror? Update(ImageMirror mirror);
    bool Delete(Guid userId, ImageMirrorTarget target, Guid id);
    bool Select(Guid userId, ImageMirrorTarget target, Guid? id);
    ImageMirror? GetSelected(Guid userId, ImageMirrorTarget target);
}

/// <summary>Development-only implementation with the same per-target selection semantics as SQLite.</summary>
public sealed class InMemoryImageMirrorRepository : IImageMirrorRepository
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, ImageMirror> _items = new();

    public IReadOnlyList<ImageMirror> List(Guid userId, ImageMirrorTarget target)
    {
        lock (_gate)
            return _items.Values.Where(x => x.UserId == userId && x.Target == target)
                .OrderBy(x => x.CreatedAt).Select(Copy).ToArray();
    }

    public ImageMirror? Find(Guid userId, ImageMirrorTarget target, Guid id)
    {
        lock (_gate)
            return _items.TryGetValue(id, out var mirror) && mirror.UserId == userId && mirror.Target == target ? Copy(mirror) : null;
    }

    public ImageMirror Create(ImageMirror mirror)
    {
        lock (_gate)
        {
            mirror.Id = Guid.NewGuid();
            mirror.CreatedAt = mirror.UpdatedAt = DateTimeOffset.UtcNow;
            var saved = Copy(mirror);
            _items[saved.Id] = saved;
            return Copy(saved);
        }
    }

    public ImageMirror? Update(ImageMirror mirror)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(mirror.Id, out var current)
                || current.UserId != mirror.UserId || current.Target != mirror.Target)
                return null;
            current.Name = mirror.Name;
            current.Endpoint = mirror.Endpoint;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            return Copy(current);
        }
    }

    public bool Delete(Guid userId, ImageMirrorTarget target, Guid id)
    {
        lock (_gate)
            return _items.TryGetValue(id, out var mirror) && mirror.UserId == userId && mirror.Target == target
                && _items.TryRemove(id, out _);
    }

    public bool Select(Guid userId, ImageMirrorTarget target, Guid? id)
    {
        lock (_gate)
        {
            if (id is { } selectedId && (!_items.TryGetValue(selectedId, out var selected)
                || selected.UserId != userId || selected.Target != target))
                return false;
            foreach (var mirror in _items.Values.Where(x => x.UserId == userId && x.Target == target))
                mirror.IsSelected = id == mirror.Id;
            return true;
        }
    }

    public ImageMirror? GetSelected(Guid userId, ImageMirrorTarget target)
    {
        lock (_gate)
            return _items.Values.FirstOrDefault(x => x.UserId == userId && x.Target == target && x.IsSelected) is { } mirror ? Copy(mirror) : null;
    }

    private static ImageMirror Copy(ImageMirror value) => new()
    {
        Id = value.Id, UserId = value.UserId, Target = value.Target, Name = value.Name,
        Endpoint = value.Endpoint, IsSelected = value.IsSelected, CreatedAt = value.CreatedAt, UpdatedAt = value.UpdatedAt,
    };
}
