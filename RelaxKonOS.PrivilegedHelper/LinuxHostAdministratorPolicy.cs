using System.Diagnostics;
using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>As root, asks sudoers whether this exact NSS account may run this installed Helper
/// as root. Exit status is used; localized sudo output is discarded. A group name is never treated
/// as sufficient proof of administrator status.</summary>
internal static class LinuxHostAdministratorPolicy
{
    public static async Task<PrivilegedOperationResult> CheckAsync(string? username, string? expectedUid)
    {
        if (!OperatingSystem.IsLinux() || geteuid() != 0)
            return Fail(PrivilegedProblemCode.HelperUnavailable);
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username[0] == '-'
            || username.Any(c => char.IsControl(c) || c is ':' or '/' or '\\')
            || !uint.TryParse(expectedUid, out var uid))
            return Fail(PrivilegedProblemCode.InvalidRequest);
        if (!MatchesNss(username, uid)) return Fail(PrivilegedProblemCode.AccessDenied);
        if (uid == 0) return new(true, HostAdministratorEligible: username == "root");

        var sudo = File.Exists("/usr/bin/sudo") ? "/usr/bin/sudo" : "/bin/sudo";
        var helper = Environment.ProcessPath;
        if (!File.Exists(sudo) || string.IsNullOrWhiteSpace(helper) || !Path.IsPathFullyQualified(helper))
            return Fail(PrivilegedProblemCode.HelperUnavailable);
        var start = new ProcessStartInfo(sudo)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add("-U");
        start.ArgumentList.Add(username);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(helper);
        TrustedProcessEnvironment.Apply(start);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return Fail(PrivilegedProblemCode.HelperUnavailable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try { await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                return Fail(PrivilegedProblemCode.TimedOut);
            }
            return new(true, HostAdministratorEligible: process.ExitCode == 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Fail(PrivilegedProblemCode.HelperUnavailable);
        }
    }

    private static bool MatchesNss(string username, uint expectedUid)
    {
        var buffer = Marshal.AllocHGlobal(1_048_576);
        try
        {
            if (getpwnam_r(username, out var entry, buffer, 1_048_576, out var found) != 0 || found == IntPtr.Zero)
                return false;
            return entry.Uid == expectedUid && string.Equals(Marshal.PtrToStringUTF8(entry.Name), username, StringComparison.Ordinal);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code)
        => new(false, code == PrivilegedProblemCode.AccessDenied ? 77 : 69, ProblemCode: code);

    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public IntPtr Name, Password;
        public uint Uid, Gid;
        public IntPtr Gecos, Directory, Shell;
    }

    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint geteuid();
    [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int getpwnam_r(string name, out Passwd entry, IntPtr buffer, nuint length, out IntPtr result);
}
