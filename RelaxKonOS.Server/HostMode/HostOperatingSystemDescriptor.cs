using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.HostMode;

/// <summary>
/// 把宿主系统归类成客户端画得出标记的少数几类。
///
/// 这是 `/server/host-operating-system` 的全部实现，也是**唯一**做这个判断的地方；判定只回答「是哪一类」。
/// <see cref="HostOperatingSystemKind.Unknown"/> 同时表示「不是这几类」与「问不出来」，客户端对两者都必须
/// 回落通用标记——宁可什么都不说，也不能猜一个错的标记。
/// </summary>
public static class HostOperatingSystemDescriptor
{
    /// <summary>Windows 11 的起始内部版本号。</summary>
    private const int Windows11MinimumBuild = 22000;

    private const string OsReleasePath = "/etc/os-release";

    public static HostOperatingSystemDto Describe() => new(Classify());

    public static HostOperatingSystemKind Classify()
    {
        if (OperatingSystem.IsWindows()) return ClassifyWindows();
        if (OperatingSystem.IsLinux()) return ClassifyLinux();
        return HostOperatingSystemKind.Unknown;
    }

    /// <summary>
    /// 工作站与 Server 只能靠 `RtlGetVersion` 的 `ProductType` 分开：`Environment.OSVersion` 在两者上都是
    /// 10.0.x。域控按 Server 归类，因为它的标记与 Server 相同，而「是域控」对界面没有别的意义。
    /// </summary>
    private static HostOperatingSystemKind ClassifyWindows()
    {
        if (WindowsWorkstationPlatform.Read() is not { } facts) return HostOperatingSystemKind.Unknown;
        return facts.ProductType switch
        {
            WindowsVersionFacts.Workstation when facts.MajorVersion != 10 => HostOperatingSystemKind.Unknown,
            WindowsVersionFacts.Workstation when facts.BuildNumber >= Windows11MinimumBuild => HostOperatingSystemKind.Windows11,
            WindowsVersionFacts.Workstation when facts.BuildNumber >= WindowsWorkstationPlatform.Windows10MinimumBuild =>
                HostOperatingSystemKind.Windows10,
            // 10.0 之前的工作站版本（Windows 8.1 及更早）没有对应标记，也不该被冒充成受支持的宿主。
            WindowsVersionFacts.Workstation => HostOperatingSystemKind.Unknown,
            WindowsVersionFacts.Server or WindowsVersionFacts.DomainController => HostOperatingSystemKind.WindowsServer,
            _ => HostOperatingSystemKind.Unknown,
        };
    }

    /// <summary>
    /// 按发行版身份分类，且只认 `ID`：Ubuntu 与 Debian 各有自己的标记，其余 Linux 宿主归入通用的 Linux 标记。
    ///
    /// 通用 Linux 标记只声明「这台宿主是 Linux」——<see cref="OperatingSystem.IsLinux"/> 已经证实这件事，
    /// 所以它不冒充任何发行版，也不从 `ID_LIKE` 之类的线索推测发行版。`/etc/os-release` 在这里的唯一用途
    /// 是把 Ubuntu 和 Debian 从其它发行版里分出来；读不到它同样是通用 Linux，不退回未知——后者表示
    /// 「问不出来」，而在 Linux 上这个问题有答案。
    /// </summary>
    private static HostOperatingSystemKind ClassifyLinux() => ReadOsReleaseId() switch
    {
        "ubuntu" => HostOperatingSystemKind.Ubuntu,
        "debian" => HostOperatingSystemKind.Debian,
        _ => HostOperatingSystemKind.Linux,
    };

    private static string? ReadOsReleaseId()
    {
        try
        {
            foreach (var line in File.ReadLines(OsReleasePath))
            {
                var separator = line.IndexOf('=');
                // 只认 `ID`，不认 `ID_LIKE`：后者描述的是「像谁」，不是「是谁」。
                if (separator <= 0 || !line.AsSpan(0, separator).Trim().Equals("ID", StringComparison.Ordinal)) continue;
                return line[(separator + 1)..].Trim().Trim('"', '\'').ToLowerInvariant();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 只读探测没有失败可处理，也不该让端点以 5xx 回答「这台机器是什么系统」。
        }
        return null;
    }
}
