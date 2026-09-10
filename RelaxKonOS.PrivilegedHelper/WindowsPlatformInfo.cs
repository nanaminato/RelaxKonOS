using System.Runtime.InteropServices;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Small native platform probe that does not depend on WMI being healthy.</summary>
internal static class WindowsPlatformInfo
{
    private const byte DomainController = 2;
    private const byte Server = 3;

    public static bool IsWindowsServer()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var version = new RtlOsVersionInfoEx { Size = Marshal.SizeOf<RtlOsVersionInfoEx>() };
        return RtlGetVersion(ref version) == 0
            && version.ProductType is DomainController or Server
            && (version.MajorVersion > 10 || version.MajorVersion == 10 && version.BuildNumber >= 17763);
    }

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
