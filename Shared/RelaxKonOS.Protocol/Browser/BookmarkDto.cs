using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Browser;

/// <summary>书签 DTO。对应 Server Domain/Bookmark 与 SQLite bookmarks 表。按用户隔离。</summary>
public sealed record BookmarkDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);
