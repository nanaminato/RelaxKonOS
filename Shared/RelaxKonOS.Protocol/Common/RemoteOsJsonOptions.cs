using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>RelaxKonOS 线协议统一序列化约定。Server MVC / SignalR 与 Client Http 共用此配置以保证 JSON 一致。</summary>
public static class RemoteOsJsonOptions
{
    /// <summary>
    /// 默认序列化选项：camelCase + 大小写不敏感 + 枚举字符串（camelCase）。
    /// 用于 REST（System.Text.Json）与 SignalR（JSON 协议）保持一致线协议。
    /// </summary>
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
