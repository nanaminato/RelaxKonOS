using System.Runtime.InteropServices;

namespace RelaxKonOS.Server.Identity;

/// <summary>
/// 宿主 Windows 的原始版本事实，与「这些事实意味着什么」分开存放。
///
/// 需要区分 Windows 10/11 工作站与 Windows Server，而两者在 <c>Environment.OSVersion</c> 上都是
/// 10.0.x，唯一可靠的判据是 <c>RtlGetVersion</c> 的 <c>ProductType</c>。读取只发生在这里，判定留给调用方，
/// 免得同一个 P/Invoke 被抄第二份。
/// </summary>
/// <param name="ProductType">`VER_NT_*`：1 工作站、2 域控、3 Server。</param>
public readonly record struct WindowsVersionFacts(byte ProductType, int MajorVersion, int BuildNumber)
{
    /// <summary>`VER_NT_WORKSTATION`。</summary>
    public const byte Workstation = 1;

    /// <summary>`VER_NT_DOMAIN_CONTROLLER`。</summary>
    public const byte DomainController = 2;

    /// <summary>`VER_NT_SERVER`。</summary>
    public const byte Server = 3;
}

/// <summary>Detects Windows 10/11 workstation editions without treating Windows Server as desktop Windows.</summary>
public static class WindowsWorkstationPlatform
{
    /// <summary>Windows 10 的起始内部版本号。</summary>
    public const int Windows10MinimumBuild = 10240;

    /// <summary>宿主版本事实，或 `null`：不在 Windows 上，或查询失败。调用方必须把 `null` 当成「问不出来」。</summary>
    public static WindowsVersionFacts? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var version = new RtlOsVersionInfoEx { Size = Marshal.SizeOf<RtlOsVersionInfoEx>() };
        if (RtlGetVersion(ref version) != 0) return null;
        return new WindowsVersionFacts(version.ProductType, version.MajorVersion, version.BuildNumber);
    }

    public static bool IsWindows10Or11Workstation() =>
        Read() is { ProductType: WindowsVersionFacts.Workstation, MajorVersion: 10 } facts
        && facts.BuildNumber >= Windows10MinimumBuild;

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RtlOsVersionInfoEx version);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfoEx
    {
        public int Size;
        public int MajorVersion;
        public int MinorVersion;
        public int BuildNumber;
        public int PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string CsdVersion;
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }
}
