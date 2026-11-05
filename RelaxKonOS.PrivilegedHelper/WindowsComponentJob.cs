using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.Platform.Processes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>SCM component ownership uses the same native Job proof as terminal workers.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsComponentJob
{
    internal static SafeFileHandle Create() => WindowsProcessTreeJob.Create();
    internal static void Attach(SafeFileHandle job, Process process) => WindowsProcessTreeJob.Attach(job, process);
    internal static Task ConfirmExitAsync(SafeFileHandle job, CancellationToken cancellationToken) =>
        WindowsProcessTreeJob.ConfirmExitAsync(job, cancellationToken);
}
