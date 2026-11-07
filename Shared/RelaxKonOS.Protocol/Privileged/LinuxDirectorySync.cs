using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RelaxKonOS.Protocol.Privileged;

/// <summary>Commits a Linux directory entry after an atomic file replacement.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxDirectorySync
{
    public static void Flush(string directory)
    {
        var fd = open(directory, 0); // O_RDONLY
        if (fd < 0) throw new IOException("Unable to open the durable state directory.");
        try
        {
            if (fsync(fd) != 0) throw new IOException("Unable to sync the durable state directory.");
        }
        finally { close(fd); }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc")]
    private static extern int close(int fd);
}
