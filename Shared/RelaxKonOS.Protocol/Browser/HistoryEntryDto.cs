using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Browser;

/// <summary>历史记录条目 DTO。对应 Server Domain/HistoryEntry 与 SQLite history_entries 表。按用户隔离。
/// 同 URL 多次访问合并为一条记录（VisitCount 累加，LastVisitedAt 更新为最近访问时间）。</summary>
public sealed record HistoryEntryDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("visitCount")] int VisitCount,
    [property: JsonPropertyName("firstVisitedAt")] DateTimeOffset FirstVisitedAt,
    [property: JsonPropertyName("lastVisitedAt")] DateTimeOffset LastVisitedAt);
