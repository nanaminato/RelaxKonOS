using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Fresh OS-owned baseline for the effective account, not Server process environment.</summary>
public static class TerminalUserEnvironment
{
    public static Dictionary<string, string> Windows(SafeAccessTokenHandle token, string home)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!CreateEnvironmentBlock(out var block, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cursor = block;
            while (Marshal.PtrToStringUni(cursor) is { Length: > 0 } entry)
            {
                var separator = entry.IndexOf('=', 1);
                if (separator > 0 && entry[0] != '=') values[entry[..separator]] = entry[(separator + 1)..];
                cursor += (entry.Length + 1) * sizeof(char);
            }
            values["USERPROFILE"] = home;
            values["HOMEDRIVE"] = Path.GetPathRoot(home)!.TrimEnd('\\');
            values["HOMEPATH"] = home[values["HOMEDRIVE"].Length..];
            values["TERM"] = "xterm-256color";
            return values;
        }
        finally { DestroyEnvironmentBlock(block); }
    }

    public static Dictionary<string, string> Linux(string home, string username, string shell)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/local/bin:/usr/bin:/bin" };
        const string path = "/etc/environment";
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("settings.environment.document_too_large_or_invalid");
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
            var buffer = new char[1024 * 1024 + 1];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count == buffer.Length) throw new InvalidDataException("settings.environment.document_too_large_or_invalid");
            var text = new string(buffer, 0, count);
            foreach (var pair in RelaxKonOS.PrivilegedHelper.LinuxEnvironmentDocument.Parse(text).Values) values[pair.Key] = pair.Value;
        }
        values["HOME"] = home; values["USER"] = values["LOGNAME"] = username;
        values["SHELL"] = shell; values["TERM"] = "xterm-256color";
        return values;
    }

    public static Dictionary<string, string> Administrator(string home)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        RelaxKonOS.Protocol.Privileged.TrustedProcessEnvironment.Apply(start);
        var values = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
        values["USERPROFILE"] = home; values["TERM"] = "xterm-256color";
        return values;
    }

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
