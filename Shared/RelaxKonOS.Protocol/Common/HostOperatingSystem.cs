using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>
/// RelaxKonOS Server 进程所在宿主的操作系统类别。
///
/// 它是**产品标记**而不是版本清单：客户端只按这个名字选中一枚图标并本地化，绝不解析服务端可能附带的
/// 文本，也不据此推断能力（能力由 <see cref="ServerCapabilitiesDto"/> 回答）。因此这里只列出客户端真的
/// 有对应标记的系统；<see cref="Unknown"/> 同时表示「不是这几类」与「问不出来」，客户端对两者都必须
/// 回落通用标记，不得猜成其中任何一个。
/// </summary>
public enum HostOperatingSystemKind
{
    Unknown,
    Ubuntu,
    Windows10,
    Windows11,
    WindowsServer
}

/// <summary>
/// 宿主系统的只读描述，供客户端为一条连接选择标记。
///
/// 名字写全（不是 `os`/`platform`）是因为协议里已有一个 <see cref="HostPlatformKind"/>：后者回答
/// 「Windows 还是 Linux」，是所有能力判定的依据；本类型回答「界面上画哪个标记」，两者不能互相替代。
/// 它不含版本号、主机名、账号或任何部署细节——那些要么已有别处回答，要么根本不该离开宿主。
/// </summary>
public sealed record HostOperatingSystemDto(
    [property: JsonPropertyName("kind")] HostOperatingSystemKind Kind);
