using System.Collections.Concurrent;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

/// <summary>浏览器书签与历史记录仓储。按用户隔离。同用户的 URL 唯一约束（bookmark）/ 合并（history）由实现保证。
/// 与 User/Workspace/Device 仓储同模式：InMemory（开发回退）与 Sqlite（默认，持久化）。</summary>
public interface IBrowserRepository
{
    // ── bookmarks ──
    IReadOnlyList<Bookmark> ListBookmarks(Guid userId);
    Bookmark UpsertBookmark(Guid userId, string title, string url);
    bool DeleteBookmark(Guid userId, Guid bookmarkId);
    int ClearBookmarks(Guid userId);

    // ── history ──
    IReadOnlyList<HistoryEntry> ListHistory(Guid userId, int limit);
    HistoryEntry UpsertHistory(Guid userId, string title, string url);
    bool DeleteHistory(Guid userId, Guid entryId);
    int ClearHistory(Guid userId);
}

/// <summary>内存实现。Singleton。并发字典按 (userId, url) 索引。内存实现，重启丢失（与 InMemory 仓储一致）。</summary>
public sealed class InMemoryBrowserRepository : IBrowserRepository
{
    private readonly ConcurrentDictionary<(Guid userId, string url), Bookmark> _bookmarks = new();
    private readonly ConcurrentDictionary<Guid, Bookmark> _bookmarkById = new();
    private readonly ConcurrentDictionary<(Guid userId, string url), HistoryEntry> _history = new();
    private readonly ConcurrentDictionary<Guid, HistoryEntry> _historyById = new();

    public IReadOnlyList<Bookmark> ListBookmarks(Guid userId)
        => _bookmarks.Where(kv => kv.Key.userId == userId)
                     .Select(kv => kv.Value)
                     .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                     .ToList();

    public Bookmark UpsertBookmark(Guid userId, string title, string url)
    {
        (Guid userId, string url) key = (userId, NormalizeUrl(url));
        if (_bookmarks.TryGetValue(key, out var existing))
        {
            // 同 URL：更新 Title（不重置 CreatedAt）
            existing.Title = string.IsNullOrWhiteSpace(title) ? existing.Title : title;
            return existing;
        }
        var bm = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = DeriveTitle(title, url),
            Url = key.url,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _bookmarks[key] = bm;
        _bookmarkById[bm.Id] = bm;
        return bm;
    }

    public bool DeleteBookmark(Guid userId, Guid bookmarkId)
    {
        if (!_bookmarkById.TryRemove(bookmarkId, out var bm) || bm.UserId != userId) return false;
        _bookmarks.TryRemove((userId, bm.Url), out _);
        return true;
    }

    public int ClearBookmarks(Guid userId)
    {
        var toRemove = _bookmarks.Where(kv => kv.Key.userId == userId).Select(kv => kv.Value.Id).ToList();
        foreach (var id in toRemove)
        {
            if (_bookmarkById.TryRemove(id, out var bm))
                _bookmarks.TryRemove((userId, bm.Url), out _);
        }
        return toRemove.Count;
    }

    public IReadOnlyList<HistoryEntry> ListHistory(Guid userId, int limit)
        => _history.Where(kv => kv.Key.userId == userId)
                   .Select(kv => kv.Value)
                   .OrderByDescending(h => h.LastVisitedAt)
                   .Take(limit <= 0 ? int.MaxValue : limit)
                   .ToList();

    public HistoryEntry UpsertHistory(Guid userId, string title, string url)
    {
        (Guid userId, string url) key = (userId, NormalizeUrl(url));
        var now = DateTimeOffset.UtcNow;
        if (_history.TryGetValue(key, out var existing))
        {
            existing.VisitCount++;
            existing.LastVisitedAt = now;
            if (!string.IsNullOrWhiteSpace(title)) existing.Title = title;
            return existing;
        }
        var h = new HistoryEntry
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = DeriveTitle(title, url),
            Url = key.url,
            VisitCount = 1,
            FirstVisitedAt = now,
            LastVisitedAt = now,
        };
        _history[key] = h;
        _historyById[h.Id] = h;
        return h;
    }

    public bool DeleteHistory(Guid userId, Guid entryId)
    {
        if (!_historyById.TryRemove(entryId, out var h) || h.UserId != userId) return false;
        _history.TryRemove((userId, h.Url), out _);
        return true;
    }

    public int ClearHistory(Guid userId)
    {
        var toRemove = _history.Where(kv => kv.Key.userId == userId).Select(kv => kv.Value.Id).ToList();
        foreach (var id in toRemove)
        {
            if (_historyById.TryRemove(id, out var h))
                _history.TryRemove((userId, h.Url), out _);
        }
        return toRemove.Count;
    }

    private static string NormalizeUrl(string url) => (url ?? string.Empty).Trim();

    private static string DeriveTitle(string title, string url)
        => string.IsNullOrWhiteSpace(title) ? (url?.Trim() ?? "Untitled") : title.Trim();
}
