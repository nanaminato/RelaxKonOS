using System.Globalization;
using System.Runtime.InteropServices;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Linux ownership metadata and bounded NSS account lookup, without shell commands.</summary>
public static class LinuxFileOwnership
{
    public static (uint? Owner, uint? Group) Read(string path)
    {
        if (!OperatingSystem.IsLinux()) return (null, null);
        if (statx(-100, path, 0, 0x7ff, out var value) != 0)
            throw new IOException("Could not read file ownership.");
        return (value.Owner, value.Group);
    }

    public static uint Resolve(string value, bool group)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("An owner and group name or numeric ID are required.");
        if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return id != uint.MaxValue ? id : throw new ArgumentException("Invalid ownership ID.");
        var record = Marshal.AllocHGlobal(256);
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            var status = group ? getgrnam_r(value, record, buffer, 65536, out var found)
                : getpwnam_r(value, record, buffer, 65536, out found);
            if (status != 0 || found == IntPtr.Zero) throw new ArgumentException("The specified owner or group does not exist.");
            id = unchecked((uint)Marshal.ReadInt32(record, IntPtr.Size * 2));
            return id != uint.MaxValue ? id : throw new ArgumentException("Invalid ownership ID.");
        }
        finally { Marshal.FreeHGlobal(record); Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct OwnershipStat
    {
        [FieldOffset(20)] public uint Owner;
        [FieldOffset(24)] public uint Group;
    }
    [DllImport("libc.so.6", SetLastError = true)] private static extern int statx(int fd, string path, int flags, uint mask, out OwnershipStat stat);
    [DllImport("libc.so.6")] private static extern int getpwnam_r(string name, IntPtr record, IntPtr buffer, nuint length, out IntPtr result);
    [DllImport("libc.so.6")] private static extern int getgrnam_r(string name, IntPtr record, IntPtr buffer, nuint length, out IntPtr result);
}
