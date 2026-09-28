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
    /// 只认 Ubuntu：别的发行版（含 Debian）没有对应标记，硬套 Ubuntu 的标记就是给用户一个错答案。
    /// `/etc/os-release` 是这类发行版的标准位置；读不到就保持未知，绝不从别的线索推发行版。
    /// </summary>
    private static HostOperatingSystemKind ClassifyLinux() =>
        ReadOsReleaseId() == "ubuntu" ? HostOperatingSystemKind.Ubuntu : HostOperatingSystemKind.Unknown;

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
