using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Server.Domain;

/// <summary>服务端 Device 领域模型。访问 Workspace 的终端设备。对应 Authentication.md §13。
/// Platform 存小写字符串（"windows"/"linux"），与 DeviceDto.Platform 对齐。</summary>
public sealed class Device
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = string.Empty;
    public DateTimeOffset? LastLoginAt { get; set; }

    public DeviceDto ToDto() => new(Id, Name, Platform, ClientVersion, LastLoginAt);
}
