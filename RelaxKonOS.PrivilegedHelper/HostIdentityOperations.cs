using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Fixed host-name operation. Windows observes the documented computer-name registry keys and stages a
/// rename through SetComputerNameEx; Linux observes <c>/etc/hostname</c> and writes through the
/// distribution's own fixed <c>hostnamectl</c> program. No shell, caller-supplied executable,
/// caller-supplied registry path or arbitrary configuration file.
/// </summary>
internal static class HostIdentityOperations
{
    private const string ActiveComputerNameKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName";
    private const string PendingComputerNameKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName";
    private const string ComputerNameValue = "ComputerName";
    private const string LinuxHostNameFile = "/etc/hostname";
    private const string LinuxHostnamectl = "/usr/bin/hostnamectl";
    private const int ComputerNamePhysicalDnsHostname = 5;

    public static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
    {
        var allowed = new PrivilegedOperationRequest(request.Operation, HostName: request.HostName,
            ExpectedRevision: request.ExpectedRevision, OperationId: request.OperationId, Correlation: request.Correlation);
        if (request != allowed || (request.Operation == PrivilegedOperationKind.HostIdentityRead
            && (request.HostName is not null || request.ExpectedRevision is not null)))
            return Failure(PrivilegedProblemCode.InvalidRequest);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return Failure(PrivilegedProblemCode.UnsupportedOperation);
        if (OperatingSystem.IsLinux() && !File.Exists(LinuxHostNameFile)) return Failure(PrivilegedProblemCode.UnsupportedOperation);
        if (OperatingSystem.IsLinux() && !File.Exists(LinuxHostnamectl)) return Failure(PrivilegedProblemCode.UnsupportedOperation);

        // The Linux Helper is a one-shot process, so a named mutex could not exclude a concurrent
        // invocation; the Server coordinator owns cross-request serialization there.
        using var writeLock = OperatingSystem.IsWindows() ? new Mutex(false, @"Global\RelaxKonOS.HostIdentity") : null;
        var held = false;
        try
        {
            if (writeLock is not null)
            {
                try { held = writeLock.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return Failure(PrivilegedProblemCode.TimedOut);
            }
            var current = await ReadAsync();
            if (request.Operation == PrivilegedOperationKind.HostIdentityRead) return new(true, HostIdentity: current);
            if (request.ExpectedRevision != current.Revision) return Failure(PrivilegedProblemCode.Conflict);
            if (HostIdentityValidation.Validate(new(request.HostName!), OperatingSystem.IsWindows()) is not null)
                return Failure(PrivilegedProblemCode.InvalidRequest);
            // The Helper re-reads the platform baseline independently of the Server's preview plan.
            await ApplyAsync(request.HostName!);
            // Windows stages the rename until the next restart, so the pending name is the confirmation.
            var observed = await ReadAsync();
            return string.Equals(observed.PendingHostName, request.HostName, StringComparison.Ordinal)
                ? new(true, HostIdentity: observed) : Failure(PrivilegedProblemCode.Conflict);
        }
        catch (InvalidDataException) { return Failure(PrivilegedProblemCode.UnsupportedOperation); }
        catch (UnauthorizedAccessException) { return Failure(PrivilegedProblemCode.AccessDenied); }
        catch (PlatformNotSupportedException) { return Failure(PrivilegedProblemCode.UnsupportedOperation); }
        catch (PlatformPolicyRejectedException) { return Failure(PrivilegedProblemCode.ResourceNotAllowed); }
        finally { if (held) writeLock!.ReleaseMutex(); }
    }

    private static async Task<HostIdentityState> ReadAsync()
    {
        if (OperatingSystem.IsWindows()) return ReadWindows();
        return await ReadLinuxAsync();
    }

    [SupportedOSPlatform("windows")]
    private static HostIdentityState ReadWindows()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var active = root.OpenSubKey(ActiveComputerNameKey);
        using var pending = root.OpenSubKey(PendingComputerNameKey);
        // ActiveComputerName is written at boot; Environment.MachineName is the equivalent process
        // view and is used only when the documented key is absent on an unusual installation.
        var activeName = active?.GetValue(ComputerNameValue) as string;
        if (string.IsNullOrWhiteSpace(activeName)) activeName = Environment.MachineName;
        var pendingName = pending?.GetValue(ComputerNameValue) as string;
        if (string.IsNullOrWhiteSpace(pendingName)) pendingName = activeName;
        return Snapshot(activeName, pendingName, HostIdentityValidation.WindowsMaximumLength, "windows-computername");
    }

    private static async Task<HostIdentityState> ReadLinuxAsync()
    {
        // /etc/hostname is the static host name systemd and the PAM stack both consume. It has no
        // staged state, so the pending name is the observed name.
        var staticName = (await File.ReadAllTextAsync(LinuxHostNameFile)).Trim();
        return Snapshot(staticName, staticName, HostIdentityValidation.LinuxMaximumLength, "etc-hostname");
    }

    private static HostIdentityState Snapshot(string activeName, string pendingName, int maximumLength, string provider)
    {
        // Observation must never reject a name RelaxKonOS did not create, so this is a structural
        // check only: a single printable label of platform-bounded length.
        if (!IsWellFormed(activeName, maximumLength) || !IsWellFormed(pendingName, maximumLength))
            throw new InvalidDataException("settings.identity.invalid_snapshot");
        return new(activeName, pendingName, maximumLength,
            SettingsRevisions.Hash(activeName + "\n" + pendingName), DateTimeOffset.UtcNow, provider);
    }

    private static bool IsWellFormed(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);

    private static async Task ApplyAsync(string hostName)
    {
        if (OperatingSystem.IsWindows())
        {
            // SetComputerNameEx is the documented rename API; the local Helper is already elevated.
            if (!SetComputerNameEx(ComputerNamePhysicalDnsHostname, hostName)) throw new PlatformPolicyRejectedException();
            return;
        }
        await RunAsync(LinuxHostnamectl, ["set-hostname", hostName]);
    }

    private static async Task RunAsync(string executable, string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("settings.identity.start_failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new IOException("settings.identity.outcome_unknown");
        }
        await error; // Never return command diagnostics which may contain host-specific data.
        if (process.ExitCode != 0) throw new IOException("settings.identity.outcome_unknown");
        await output;
    }

    // A domain membership or security policy rejecting the name is a deterministic refusal, not an
    // unknown outcome, so it must not be reported as a transport failure.
    private sealed class PlatformPolicyRejectedException : Exception;

    private static PrivilegedOperationResult Failure(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetComputerNameEx(int computerNameFormat, string computerName);
}
