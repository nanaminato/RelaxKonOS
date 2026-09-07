using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Server.Files;

/// <summary>Only an OS rename: never lets a native API silently do an uncancellable cross-volume copy.</summary>
internal static class FileOperationRename
{
    public static bool TryMove(string source, string destination, bool replace)
    {
        int error;
        if (OperatingSystem.IsWindows())
        {
            if (MoveFileEx(source, destination, replace ? 1u : 0u)) return true;
            error = Marshal.GetLastPInvokeError();
            if (error == 17) return false; // ERROR_NOT_SAME_DEVICE: use the cancellable stream path.
            if (error == 5) throw new UnauthorizedAccessException(new Win32Exception(error).Message);
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                // RENAME_NOREPLACE closes the existence-check race without unlinking an existing target.
                if (RenameAt2(-100, source, -100, destination, replace ? 0u : 1u) == 0)
                {
                    if (File.Exists(source)) throw new IOException("The source still exists after rename; verify the move result.");
                    return true;
                }
                error = Marshal.GetLastPInvokeError();
                if (error is 18 or 22 or 38 or 95) return false; // Cross-device or unavailable rename flags.
                if (error is 1 or 13) throw new UnauthorizedAccessException(new Win32Exception(error).Message);
            }
            catch (EntryPointNotFoundException) { return false; }
        }
        else return false;
        throw new IOException(new Win32Exception(error).Message, unchecked((int)0x80070000) | error);
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int sourceDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int destinationDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
}
