using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelaxKonOS.PrivilegedHelper;

internal static class TailscaleControlProtection
{
    internal static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RelaxKonOS-TailscaleControl")
        : "/var/lib/relaxkonos-tailscale";

    internal static void PrepareRoot(string root)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Require(Path.GetDirectoryName(root)!);
        if (!Directory.Exists(root))
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                foreach (var kind in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(root).Create(security);
            }
            else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Require(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root)) Require(path);
    }

    internal static void RequireExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            var parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Require(parent); Require(Path.Combine(parent, "Tailscale")); Require(Path.Combine(parent, "Tailscale", "tailscale.exe"));
        }
        else if (OperatingSystem.IsLinux())
        {
            Require("/usr"); Require("/usr/bin"); Require("/usr/bin/tailscale");
        }
        else throw new PlatformNotSupportedException();
    }

    internal static void Require(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException();
        if (OperatingSystem.IsWindows())
        {
            for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            {
                FileSystemSecurity acl = Directory.Exists(current) ? new DirectoryInfo(current).GetAccessControl() : new FileInfo(current).GetAccessControl();
                RequireWindowsDescriptor(new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(),0),current == Path.GetFullPath(path));
            }
        }
        else
        {
            for (var ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if (statx(-100, ancestor, 0x100, 0x1f, out var parent) != 0 || (parent.Mask & 0xb) != 0xb || parent.Uid != 0 || (parent.Mode & 0x12) != 0)
                    throw new UnauthorizedAccessException();
            if (statx(-100, path, 0x100, 0x1f, out var state) != 0 || (state.Mask & 0xb) != 0xb || state.Uid != 0 || (state.Mode & 0x12) != 0)
                throw new UnauthorizedAccessException();
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void RequireWindowsDescriptor(RawSecurityDescriptor descriptor,bool leaf)
    {
        static bool Trusted(string? sid) => sid is "S-1-5-18" or "S-1-5-32-544"
            or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        if (!Trusted(descriptor.Owner?.Value) || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0
            || descriptor.DiscretionaryAcl is null) throw new UnauthorizedAccessException();
        var forbidden = (int)(FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | 0x10000000 | 0x40000000; // GENERIC_ALL / WRITE
        if (leaf) forbidden |= (int)FileSystemRights.Write;
        foreach (GenericAce entry in descriptor.DiscretionaryAcl)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not QualifiedAce ace) throw new UnauthorizedAccessException();
            if (ace.AceQualifier == AceQualifier.AccessDenied) continue;
            if (ace.AceQualifier != AceQualifier.AccessAllowed) throw new UnauthorizedAccessException();
            if (!Trusted(ace.SecurityIdentifier.Value) && (ace.AccessMask & forbidden) != 0) throw new UnauthorizedAccessException();
        }
    }
    internal static void FlushDirectory(string root)
    {
        var fd = open(root, 0x10000); // O_DIRECTORY | O_RDONLY
        if (fd < 0) throw new IOException("tailscale.receipt_sync_failed");
        try { if (fsync(fd) != 0) throw new IOException("tailscale.receipt_sync_failed"); }
        finally { close(fd); }
    }
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc")] private static extern int close(int fd);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int directory, string path, int flags, uint mask, out Statx state);
}
