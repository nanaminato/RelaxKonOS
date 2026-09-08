using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

/// <summary>浏览器书签/历史记录仓储的 EF Core + SQLite 实现。Scoped（依赖 Scoped DbContext）。
/// 对应 InMemoryBrowserRepository。同用户 URL 唯一（bookmark）由唯一索引保证；历史合并通过 Find+Update 实现。</summary>
public sealed class SqliteBrowserRepository : IBrowserRepository
{
    private readonly RelaxKonOSDbContext _db;

    public SqliteBrowserRepository(RelaxKonOSDbContext db) => _db = db;

    // ── bookmarks ──

    public IReadOnlyList<Bookmark> ListBookmarks(Guid userId)
        => _db.Bookmarks.AsNoTracking()
                .Where(b => b.UserId == userId)
                .OrderBy(b => b.Title)
                .ToList();

    public Bookmark UpsertBookmark(Guid userId, string title, string url)
    {
        var normalizedUrl = (url ?? string.Empty).Trim();
        var existing = _db.Bookmarks.FirstOrDefault(b => b.UserId == userId && b.Url == normalizedUrl);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(title)) existing.Title = title.Trim();
            _db.SaveChanges();
            return existing;
        }
        var bm = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = string.IsNullOrWhiteSpace(title) ? (normalizedUrl.Length > 0 ? normalizedUrl : "Untitled") : title.Trim(),
            Url = normalizedUrl,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Bookmarks.Add(bm);
        _db.SaveChanges();
        return bm;
    }

    public bool DeleteBookmark(Guid userId, Guid bookmarkId)
    {
        var bm = _db.Bookmarks.FirstOrDefault(b => b.Id == bookmarkId && b.UserId == userId);
        if (bm is null) return false;
        _db.Bookmarks.Remove(bm);
        _db.SaveChanges();
        return true;
    }

    public int ClearBookmarks(Guid userId)
    {
        var rows = _db.Bookmarks.Where(b => b.UserId == userId).ExecuteDelete();
        return rows;
    }

    // ── history ──

    public IReadOnlyList<HistoryEntry> ListHistory(Guid userId, int limit)
    {
        // SQLite's EF Core provider cannot translate an ORDER BY over DateTimeOffset. These
        // values are written in UTC, so ordering their ISO-8601 text representation is chronological.
        return (limit <= 0
                ? _db.History.FromSqlInterpolated($"""
                    SELECT * FROM "history_entries"
                    WHERE "UserId" = {userId}
                    ORDER BY "LastVisitedAt" DESC
                    """)
                : _db.History.FromSqlInterpolated($"""
                    SELECT * FROM "history_entries"
                    WHERE "UserId" = {userId}
                    ORDER BY "LastVisitedAt" DESC
                    LIMIT {limit}
                    """))
            .AsNoTracking()
            .ToList();
    }

    public HistoryEntry UpsertHistory(Guid userId, string title, string url)
    {
        var normalizedUrl = (url ?? string.Empty).Trim();
        var now = DateTimeOffset.UtcNow;
        var existing = _db.History.FirstOrDefault(h => h.UserId == userId && h.Url == normalizedUrl);
        if (existing is not null)
        {
            existing.VisitCount++;
            existing.LastVisitedAt = now;
            if (!string.IsNullOrWhiteSpace(title)) existing.Title = title.Trim();
            _db.SaveChanges();
            return existing;
        }
        var h = new HistoryEntry
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = string.IsNullOrWhiteSpace(title) ? (normalizedUrl.Length > 0 ? normalizedUrl : "Untitled") : title.Trim(),
            Url = normalizedUrl,
            VisitCount = 1,
            FirstVisitedAt = now,
            LastVisitedAt = now,
        };
        _db.History.Add(h);
        _db.SaveChanges();
        return h;
    }

    public bool DeleteHistory(Guid userId, Guid entryId)
    {
        var h = _db.History.FirstOrDefault(x => x.Id == entryId && x.UserId == userId);
        if (h is null) return false;
        _db.History.Remove(h);
        _db.SaveChanges();
        return true;
    }

    public int ClearHistory(Guid userId)
    {
        var rows = _db.History.Where(h => h.UserId == userId).ExecuteDelete();
        return rows;
    }
}
