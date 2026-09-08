using RelaxKonOS.Protocol.Browser;

namespace RelaxKonOS.Server.Domain;

/// <summary>书签领域模型。按用户隔离。对应 SQLite bookmarks 表。同用户下 URL 唯一（应用层保证）。</summary>
public sealed class Bookmark
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public BookmarkDto ToDto() => new(Id, UserId, Title, Url, CreatedAt);
}

/// <summary>历史访问记录领域模型。按用户隔离。对应 SQLite history_entries 表。
/// 同 URL 多次访问合并为一条（VisitCount 累加，LastVisitedAt 取最近）。</summary>
public sealed class HistoryEntry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int VisitCount { get; set; }
    public DateTimeOffset FirstVisitedAt { get; set; }
    public DateTimeOffset LastVisitedAt { get; set; }

    public HistoryEntryDto ToDto() => new(Id, UserId, Title, Url, VisitCount, FirstVisitedAt, LastVisitedAt);
}
