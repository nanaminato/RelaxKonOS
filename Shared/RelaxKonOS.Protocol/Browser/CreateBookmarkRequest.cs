using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Browser;

/// <summary>新增书签请求。Title 为空时由服务端从 URL 推导。同用户下 URL 重复则更新 Title（不重复插入）。</summary>
public sealed record CreateBookmarkRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url);
