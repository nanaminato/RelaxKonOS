using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using RelaxKonOS.Server.Terminal;
using RelaxKonOS.Protocol.UserExecution;

/// <summary>Run only in its own Linux root process; it must never alter the aggregate test runner's identity.</summary>
internal static class TerminalTreePrivilegeChecks
{
    internal static async Task RunAsync()
    {
        RequireRoot();
        var owner = new LinuxTerminalProcessTree();
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--terminal-tree-privilege-worker");
        using var worker = Process.Start(info) ?? throw new IOException("Cannot start the exclusive native permission worker.");
        var error = worker.StandardError.ReadToEndAsync();
        try
        {
            var frame = await UserTerminalStreamProtocol.ReadAsync(worker.StandardOutput.BaseStream).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (frame?.Kind != UserTerminalFrameKind.TreeFaulted)
                throw new InvalidOperationException("The actual EPERM must emit TreeFaulted, never a successful Exited acknowledgement.");
            await Task.Delay(100);
            if (worker.HasExited) throw new InvalidOperationException("A faulted subreaper must retain its native responsibility, not exit.");
            Console.WriteLine("PASS TERMINAL PRIVILEGE: actual EPERM emits TreeFaulted and keeps its dedicated worker alive without cleanup replay.");
            // This explicit, privileged test-owner action ends the otherwise retained fault. It
            // is not automatic production recovery and does not use an unbound numeric-PID kill.
            using (var observed = LinuxTerminalProcessExit.Capture(worker.Id))
                observed.Terminate();
            // This Process owns the worker's reap; the pidfd only binds the privileged signal.
            // Reaping it through waitid first would steal System.Diagnostics.Process's child.
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await owner.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var errors = await error.WaitAsync(TimeSpan.FromSeconds(5));
            var marker = errors.Split('\n').SingleOrDefault(line => line.StartsWith("TERMINAL PRIVILEGE CHILD pid=", StringComparison.Ordinal));
            if (marker is null || !int.TryParse(marker.AsSpan("TERMINAL PRIVILEGE CHILD pid=".Length).Trim(), out var pid)
                || Directory.Exists($"/proc/{pid}") || !errors.Contains("EPERM verified", StringComparison.Ordinal))
                throw new InvalidOperationException("Native elevated descendant responsibility was not preserved and subsequently cleaned by its actual owner.");
            Console.WriteLine("PASS TERMINAL PRIVILEGE OWNER: the privileged outer subreaper adopted, terminated and reaped its exact root descendant; /proc is absent.");
        }
        finally
        {
            // Handles timeout/failed assertions too, using exact adopted ownership rather than
            // a shared UID sweep or any numeric-PID kill fallback.
            await owner.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    internal static async Task RunWorkerAsync()
    {
        RequireRoot();
        var owner = new LinuxTerminalProcessTree();
        using var child = Process.Start(new ProcessStartInfo("/usr/bin/sleep", "120") { UseShellExecute = false })
            ?? throw new IOException("Cannot start the actual root-owned descendant.");
        Console.Error.WriteLine("TERMINAL PRIVILEGE CHILD pid=" + child.Id);
        Console.Error.Flush();
        // Drop supplementary groups and all three real/effective/saved IDs permanently. The
        // fixture changes only this dedicated process, and creates no persistent local account.
        if (setgroups(0, IntPtr.Zero) != 0 || setresgid(600000, 600000, 600000) != 0 || setresuid(600000, 600000, 600000) != 0
            || geteuid() != 600000 || getuid() != 600000)
            throw new IOException("Permanent fixture privilege drop failed.");
        try
        {
            await owner.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("A real EPERM was incorrectly reported as successful tree completion.");
        }
        catch (IOException failure) when (failure.Message == "terminal.process_tree_signal_unverified"
            && failure.InnerException is Win32Exception { NativeErrorCode: 1 })
        {
            if (!Directory.Exists($"/proc/{child.Id}") || child.HasExited)
                throw new InvalidOperationException("The elevated descendant did not remain a live unresolved responsibility.");
            Console.Error.WriteLine("EPERM verified: root descendant remains an unresolved responsibility.");
            await LinuxTerminalWorker.RetainFaultedTreeAsync(Console.OpenStandardOutput(), failure);
        }
    }

    private static void RequireRoot()
    {
        if (!OperatingSystem.IsLinux() || geteuid() != 0)
            throw new InvalidOperationException("Terminal permission-boundary acceptance requires its own Linux root process.");
    }
    [DllImport("libc", SetLastError = true)] private static extern int setgroups(nuint size, IntPtr groups);
    [DllImport("libc", SetLastError = true)] private static extern int setresgid(uint real, uint effective, uint saved);
    [DllImport("libc", SetLastError = true)] private static extern int setresuid(uint real, uint effective, uint saved);
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern uint getuid();
}
