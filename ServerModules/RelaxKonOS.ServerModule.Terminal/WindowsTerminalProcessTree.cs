using Microsoft.Win32.SafeHandles;
using RelaxKonOS.Platform.Processes;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Terminal owner of the shared non-breakaway Windows Job proof.</summary>
internal sealed class WindowsTerminalProcessTree : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly object _gate = new();
    private Task? _stopping;
    internal WindowsTerminalProcessTree()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _job = WindowsProcessTreeJob.Create();
    }
    internal void Attach(IntPtr process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var borrowed = new SafeFileHandle(process, ownsHandle: false); WindowsProcessTreeJob.Attach(_job, borrowed);
    }
    internal Task StopAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        lock (_gate) return _stopping ??= WindowsProcessTreeJob.ConfirmExitAsync(_job, CancellationToken.None);
    }
    internal void Terminate() => _ = StopAsync();
    internal void WaitUntilEmpty() => StopAsync().GetAwaiter().GetResult();
    public void Dispose() => _job.Dispose();
}
