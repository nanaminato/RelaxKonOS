using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal static class RuntimeSourceProtection
{
    internal static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RelaxKonOS-RuntimeTrust")
        : "/etc/relaxkonos/runtime-sources";

    internal static RuntimeSourceStore Store() => new(Root, Protect);

    internal static bool IsAdministrator()
    {
        if (OperatingSystem.IsLinux()) return geteuid() == 0;
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static void Protect(string directory)
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException();
        RuntimeSourceStore.RequireNoLinks(directory);
        if (OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(directory)) WindowsComponentService.ProtectDirectory(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Prepend(directory))
            {
                RuntimeSourceStore.RequireNoLinks(path);
                FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl()
                    : new FileInfo(path).GetAccessControl();
                if (acl.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                    throw new UnauthorizedAccessException();
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow
                        && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete
                            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0
                        && rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                        throw new UnauthorizedAccessException();
            }
            return;
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var parent = Path.GetDirectoryName(directory)!;
        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RequireRootOwned(parent);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Prepend(directory)) RequireRootOwned(path);
    }

    private static void RequireRootOwned(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        if (statx(-100, path, 0x100, 0x1f, out var state) != 0 || (state.Mask & 0xb) != 0xb
            || state.Uid != 0 || (state.Mode & 0x12) != 0) // no group/other write
            throw new UnauthorizedAccessException();
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int directory, string path, int flags, uint mask, out Statx state);
    [DllImport("libc")] private static extern uint geteuid();
}
