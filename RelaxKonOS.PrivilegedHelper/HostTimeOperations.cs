using System.Diagnostics;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed platform timezone operation. No shell, caller-supplied executable or argument program.</summary>
internal static class HostTimeOperations
{
    public static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
    {
        var allowed = new PrivilegedOperationRequest(request.Operation, TimeZoneId: request.TimeZoneId,
            ExpectedRevision: request.ExpectedRevision, OperationId: request.OperationId);
        if (request != allowed || (request.Operation == PrivilegedOperationKind.HostTimeRead
            && (request.TimeZoneId is not null || request.ExpectedRevision is not null)))
            return Failure(PrivilegedProblemCode.InvalidRequest);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return Failure(PrivilegedProblemCode.UnsupportedOperation);
        var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "tzutil.exe") : "/usr/bin/timedatectl";
        if (!File.Exists(executable)) return Failure(PrivilegedProblemCode.UnsupportedOperation);
        var current = await ReadAsync(executable);
        if (request.Operation == PrivilegedOperationKind.HostTimeRead) return new(true, HostTime: current);
        if (request.ExpectedRevision != current.Revision) return Failure(PrivilegedProblemCode.Conflict);
        if (request.TimeZoneId is not { Length: > 0 and <= 256 } zone
            || !current.AvailableTimeZoneIds.Contains(zone, StringComparer.Ordinal)) return Failure(PrivilegedProblemCode.InvalidRequest);
        // The helper re-reads the OS baseline independently of the Server's preview.
        await RunAsync(executable, OperatingSystem.IsWindows() ? ["/s", zone] : ["set-timezone", zone]);
        var observed = await ReadAsync(executable);
        return observed.TimeZoneId == zone ? new(true, HostTime: observed) : Failure(PrivilegedProblemCode.Conflict);
    }

    private static async Task<HostTimeState> ReadAsync(string executable)
    {
        var zone = (await RunAsync(executable, OperatingSystem.IsWindows() ? ["/g"] : ["show", "--property=Timezone", "--value"])).Trim();
        if (zone.Length is 0 or > 256 || zone.Any(char.IsControl)) throw new InvalidDataException("settings.time.invalid_snapshot");
        TimeZoneInfo.ClearCachedData();
        var ids = TimeZoneInfo.GetSystemTimeZones().Select(value => value.Id).Order(StringComparer.Ordinal).ToArray();
        return new(zone, ids, SettingsRevisions.Hash(zone), DateTimeOffset.UtcNow,
            OperatingSystem.IsWindows() ? "windows-tzutil" : "systemd-timedated");
    }

    private static async Task<string> RunAsync(string executable, string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("settings.time.start_failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new IOException("settings.time.outcome_unknown");
        }
        await error; // Never return command diagnostics which may contain host-specific data.
        if (process.ExitCode != 0) throw new IOException("settings.time.platform_rejected");
        return await output;
    }

    private static PrivilegedOperationResult Failure(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
}
