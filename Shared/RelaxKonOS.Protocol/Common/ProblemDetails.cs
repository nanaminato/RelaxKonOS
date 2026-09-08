using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>统一错误响应（RFC 7807 子集）。所有 REST 错误返回此结构。</summary>
public sealed record ProblemDetails(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("traceId")] string? TraceId);
