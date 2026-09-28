using System.Runtime.InteropServices;

namespace RelaxKonOS.Server.Identity;

/// <summary>Detects Windows 10/11 workstation editions without treating Windows Server as desktop Windows.</summary>
public static class WindowsWorkstationPlatform
{
    private const byte Workstation = 1;

    public static bool IsWindows10Or11Workstation()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var version = new RtlOsVersionInfoEx { Size = Marshal.SizeOf<RtlOsVersionInfoEx>() };
        return RtlGetVersion(ref version) == 0
            && version.ProductType == Workstation
            && version.MajorVersion == 10
            && version.BuildNumber >= 10240;
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
