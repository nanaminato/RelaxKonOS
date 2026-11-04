using System.Runtime.InteropServices;

namespace RelaxKonOS.ModulePackaging;

/// <summary>Portable package target derived from the executing OS, architecture and libc, never a distro RID override.</summary>
public static class ModulePackagePlatform
{
    public static string CurrentRid
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64", Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException("Unsupported module package architecture.")
            };
            if (OperatingSystem.IsWindows()) return "win-" + arch;
            if (OperatingSystem.IsMacOS()) return "osx-" + arch;
            if (OperatingSystem.IsLinux())
            {
                var loader = arch == "x64" ? "/lib/ld-musl-x86_64.so.1" : "/lib/ld-musl-aarch64.so.1";
                return (File.Exists(loader) ? "linux-musl-" : "linux-") + arch;
            }
            throw new PlatformNotSupportedException("Unsupported module package operating system.");
        }
    }
}
