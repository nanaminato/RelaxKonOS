using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Browser;

/// <summary>记录一次历史访问请求。Title 为空时由服务端从 URL 推导。同 URL 已存在则 VisitCount++ 且更新 LastVisitedAt 与 Title。</summary>
public sealed record CreateHistoryEntryRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url);
