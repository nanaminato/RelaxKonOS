using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed partial class LinuxMihomoArchiveHost
{
    private sealed record RegistrationRestore(Guid OperationId, Registration Previous, Registration Restored);
    private sealed record RestoredProcess(Guid OperationId, string ConfigurationIdentity, MihomoTunProcess Process);
    private sealed record RestoredStartDispatch(Guid OperationId, Guid BootId, uint JobId, ulong DispatchedAt);
    private sealed record RestoredStartJob(Guid OperationId, Guid BootId, uint JobId, Guid InvocationId);

    private async Task ConfigurePublicationTunAsync(RuntimePendingConfiguration pending, string binary, string data,
        CancellationToken ct)
    {
        var operation = pending.OperationId;
        var identity = pending.Plan.CandidateIdentity;
        RequireProtected(binary);
        RequireProtected(data);
        var previous = await ReadRegistrationAsync(ct) ?? throw new RuntimePolicyException("runtime.network_registration_conflict");
        var state = await StateAsync(ct);
        if (previous.TunOperation is not null || previous.Binary != binary || previous.Data != data
            || previous.ConfigurationHash != pending.Plan.PreviousIdentity || previous.BinaryHash != await DigestAsync(binary, ct)
            || state.GetValueOrDefault("MainPID") != "0" || state.GetValueOrDefault("ActiveState") != "inactive"
            || state.GetValueOrDefault("UnitFileState") != "disabled")
            Fail("network_registration_conflict");
        RequireProtected(Root + "/active.yaml");
        if (!string.Equals(await DigestAsync(Root + "/active.yaml", ct), identity, StringComparison.OrdinalIgnoreCase))
            Fail("configuration_drift");
        var candidate = previous with { TunOperation = operation, ConfigurationHash = identity };
        var path = ActivationRegistrationPath(operation);
        RuntimeSourceStore.RequireNoLinks(path);
        if (Path.Exists(path)) Fail("network_registration_conflict");
        await WriteRestoreFileAsync(operation, path, RegistrationRecord(operation, previous, candidate),
            UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        await WriteRestoreFileAsync(operation, RegistrationPath, JsonSerializer.Serialize(candidate),
            UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        await WriteRestoreFileAsync(operation, UnitPath, UnitText(candidate),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ct);
        await CommandAsync("daemon-reload", ct, false);
        await StateAsync(ct);
    }

    internal async Task<bool> RestoreTunRegistrationAsync(RuntimePendingConfiguration pending, string binary, string data,
        string version, bool beforeNetwork, bool resumeOnly, CancellationToken ct)
    {
        var operation = pending.OperationId;
        var directory = Path.Combine(Root, "operations", operation.ToString("N"));
        var path = Path.Combine(directory, "registration-restore.json");
        RuntimeSourceStore.RequireNoLinks(path);
        var existing = Path.Exists(path);
        if (!existing && resumeOnly && !beforeNetwork) return false;

        var configuration = Root + "/active.yaml";
        var identity = pending.Plan.PreviousIdentity;
        ValidatePaths(binary, data, configuration, identity);
        RequireProtected(binary);
        RequireProtected(data);
        RequireProtected(configuration);
        var actualIdentity = await DigestAsync(configuration, ct);
        var previousConfiguration = string.Equals(actualIdentity, identity, StringComparison.OrdinalIgnoreCase);
        var candidateBeforeRestore = !existing && resumeOnly
            && string.Equals(actualIdentity, pending.Plan.CandidateIdentity, StringComparison.OrdinalIgnoreCase);
        if (pending.Plan.NetworkIntentIdentity is null || pending.Snapshot is not { BootEnabled: false, Running: true }
            || !previousConfiguration && !candidateBeforeRestore)
            Fail("configuration_drift");
        var digest = await DigestAsync(binary, ct);
        var previous = new Registration(binary, data, digest, pending.Plan.CandidateIdentity, version, operation);
        var restored = previous with { TunOperation = null, ConfigurationHash = identity };
        var expected = RegistrationRecord(operation, previous, restored);
        var activated = beforeNetwork && await ReadActivationRegistrationAsync(operation, restored, previous, ct);
        if (existing)
        {
            if (await ReadRestoreFileAsync(path, ct) != expected) Fail("network_registration_conflict");
            MihomoRuntimeData.SyncConfigurationDirectory(directory);
        }
        else if (!beforeNetwork && await ReadRegistrationAsync(ct) != previous)
            Fail("network_registration_conflict");

        var registration = await ReadRestoreFileAsync(RegistrationPath, ct);
        var unit = await ReadRestoreFileAsync(UnitPath, ct);
        var previousRegistration = registration == JsonSerializer.Serialize(previous);
        var restoredRegistration = registration == JsonSerializer.Serialize(restored);
        var previousUnit = unit == UnitText(previous);
        var restoredUnit = unit == UnitText(restored);
        var allowed = beforeNetwork
            ? restoredRegistration && restoredUnit || activated && previousRegistration && (previousUnit || restoredUnit)
            : previousRegistration && previousUnit || restoredRegistration && (previousUnit || restoredUnit);
        if (!allowed)
            Fail("network_registration_conflict");

        var state = await StateAsync(ct, allowReload: existing || activated);
        if (state.GetValueOrDefault("UnitFileState") != "disabled") Fail("state_unknown");
        if (state.GetValueOrDefault("MainPID") != "0" || state.GetValueOrDefault("ActiveState") is not ("inactive" or "failed"))
        {
            if (existing && restoredRegistration && restoredUnit && state.GetValueOrDefault("NeedDaemonReload") == "no"
                && await ReadRestoreFileAsync(RestoredStartPath(operation), ct) == operation.ToString("N"))
            {
                await CompleteRestoredJobObservationAsync(operation, restored, ct);
                await VerifyRestoredTunProcessAsync(operation, ct);
                return true;
            }
            Fail("state_unknown");
        }
        RuntimeSourceStore.RequireNoLinks(RestoredStartPath(operation));
        if (Path.Exists(RestoredStartPath(operation))) Fail("network_restored_start_unconfirmed");
        if (!existing && resumeOnly) return false;

        if (!existing)
            await WriteRestoreFileAsync(operation, path, expected, UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        // Reverse the pre-network transition through the same recoverable pair: candidate registration and old unit.
        if (!beforeNetwork) await RestoreRegistrationAsync();
        if (previousUnit)
            await WriteRestoreFileAsync(operation, UnitPath, UnitText(restored),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ct);
        else SyncExistingRestoreFile(operation, UnitPath);
        if (beforeNetwork) await RestoreRegistrationAsync();
        if (previousUnit || state.GetValueOrDefault("NeedDaemonReload") == "yes")
            await CommandAsync("daemon-reload", ct, false);
        if ((await StateAsync(ct)).GetValueOrDefault("ActiveState") == "failed")
            await CommandAsync("reset-failed", ct);
        return true;

        async Task RestoreRegistrationAsync()
        {
            if (previousRegistration)
                await WriteRestoreFileAsync(operation, RegistrationPath, JsonSerializer.Serialize(restored),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
            else SyncExistingRestoreFile(operation, RegistrationPath);
        }
    }

    private static string RegistrationRecord(Guid operation, Registration previous, Registration restored) =>
        JsonSerializer.Serialize(new RegistrationRestore(operation, previous, restored));

    private static string ActivationRegistrationPath(Guid operation) =>
        Path.Combine(Root, "operations", operation.ToString("N"), "registration-activate.json");

    private static async Task<bool> ReadActivationRegistrationAsync(Guid operation, Registration previous,
        Registration candidate, CancellationToken ct)
    {
        var path = ActivationRegistrationPath(operation);
        RuntimeSourceStore.RequireNoLinks(path);
        if (!Path.Exists(path)) return false;
        if (await ReadRestoreFileAsync(path, ct) != RegistrationRecord(operation, previous, candidate))
            Fail("network_registration_conflict");
        MihomoRuntimeData.SyncConfigurationDirectory(Path.GetDirectoryName(path)!);
        return true;
    }

    private static async Task ArmRestoredTunStartAsync(Guid operation, CancellationToken ct)
    {
        var path = RestoredStartPath(operation);
        RuntimeSourceStore.RequireNoLinks(path);
        if (Path.Exists(path)) Fail("network_restored_start_unconfirmed");
        await WriteRestoreFileAsync(operation, path, operation.ToString("N"), UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
    }

    private static string RestoredStartPath(Guid operation) =>
        Path.Combine(Root, "operations", operation.ToString("N"), "registration-start-dispatched");

    private static string RestoredProcessPath(Guid operation) =>
        Path.Combine(Root, "operations", operation.ToString("N"), "registration-start-process.json");

    private static string RestoredJobPath(Guid operation) =>
        Path.Combine(Root, "operations", operation.ToString("N"), "registration-start-job.json");

    private static string RestoredJobDispatchPath(Guid operation) =>
        Path.Combine(Root, "operations", operation.ToString("N"), "registration-start-job-dispatched.json");

    private static ulong MonotonicMicroseconds() =>
        (ulong)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);

    internal static async Task RequireRestoredJobJournalAsync()
    {
        var boot = LinuxMihomoTunCheckpoint.BootId();
        var journal = await RunAsync("/usr/bin/journalctl", ["--quiet", "--no-pager", "--output=json",
            "--output-fields=_BOOT_ID", "--lines=1", "_PID=1", "_UID=0", $"_BOOT_ID={boot:N}"]);
        if (string.IsNullOrWhiteSpace(journal)) Fail("network_restored_start_unconfirmed");
    }

    private static async Task<RestoredStartJob> StartRestoredJobAsync(Guid operation, CancellationToken ct)
    {
        var path = RestoredJobPath(operation);
        var dispatchPath = RestoredJobDispatchPath(operation);
        RuntimeSourceStore.RequireNoLinks(path);
        RuntimeSourceStore.RequireNoLinks(dispatchPath);
        if (Path.Exists(path) || Path.Exists(dispatchPath)) Fail("network_restored_start_unconfirmed");
        var boot = LinuxMihomoTunCheckpoint.BootId();
        var started = MonotonicMicroseconds();
        var reply = await RunAsync("/usr/bin/busctl", ["--system", "--timeout=5s", "--json=short", "call",
            "org.freedesktop.systemd1", "/org/freedesktop/systemd1", "org.freedesktop.systemd1.Manager",
            "StartUnit", "ss", Unit, "fail"]);
        using var response = JsonDocument.Parse(reply);
        var job = response.RootElement.GetProperty("data");
        const string prefix = "/org/freedesktop/systemd1/job/";
        if (response.RootElement.GetProperty("type").GetString() != "o" || job.GetArrayLength() != 1
            || job[0].GetString() is not { } objectPath || !objectPath.StartsWith(prefix, StringComparison.Ordinal)
            || !uint.TryParse(objectPath.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id == 0)
            throw new RuntimePolicyException("runtime.network_restored_start_unconfirmed");
        var dispatch = new RestoredStartDispatch(operation, boot, id, started);
        await WriteRestoreFileAsync(operation, dispatchPath, JsonSerializer.Serialize(dispatch),
            UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        var receipt = await ReadRestoredJobResultAsync(dispatch, ct);
        await WriteRestoreFileAsync(operation, path, JsonSerializer.Serialize(receipt),
            UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        return receipt;
    }

    private static async Task<RestoredStartJob> ReadRestoredJobResultAsync(RestoredStartDispatch dispatch, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var journal = await RunAsync("/usr/bin/journalctl", ["--quiet", "--no-pager", "--output=json",
                "--output-fields=INVOCATION_ID", "--lines=2", "_PID=1", "_UID=0", $"_BOOT_ID={dispatch.BootId:N}",
                $"UNIT={Unit}", $"JOB_ID={dispatch.JobId.ToString(CultureInfo.InvariantCulture)}", "JOB_TYPE=start", "JOB_RESULT=done"]);
            var entries = journal.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length > 1) Fail("network_restored_start_unconfirmed");
            if (entries.Length == 1)
            {
                using var entry = JsonDocument.Parse(entries[0]);
                var fields = entry.RootElement;
                if (!fields.TryGetProperty("INVOCATION_ID", out var value) || value.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(value.GetString(), "N", out var invocation) || invocation == Guid.Empty
                    || !ulong.TryParse(fields.GetProperty("__MONOTONIC_TIMESTAMP").GetString(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var timestamp) || timestamp < dispatch.DispatchedAt)
                    throw new RuntimePolicyException("runtime.network_restored_start_unconfirmed");
                return new(dispatch.OperationId, dispatch.BootId, dispatch.JobId, invocation);
            }
            await Task.Delay(100, ct);
        }
        throw new RuntimePolicyException("runtime.network_restored_start_unconfirmed");
    }

    private static async Task<RestoredStartDispatch> ReadRestoredJobDispatchAsync(Guid operation, CancellationToken ct)
    {
        var path = RestoredJobDispatchPath(operation);
        RuntimeSourceStore.RequireNoLinks(path);
        if (!File.Exists(path)) throw new RuntimePolicyException("runtime.network_restored_start_unconfirmed");
        var text = await ReadRestoreFileAsync(path, ct);
        var dispatch = JsonSerializer.Deserialize<RestoredStartDispatch>(text);
        if (dispatch is null || dispatch.OperationId != operation || dispatch.BootId != LinuxMihomoTunCheckpoint.BootId()
            || dispatch.JobId == 0 || dispatch.DispatchedAt == 0 || dispatch.DispatchedAt > MonotonicMicroseconds()
            || JsonSerializer.Serialize(dispatch) != text)
            throw new RuntimePolicyException("runtime.network_restored_start_unconfirmed");
        return dispatch;
    }

    private static async Task CompleteRestoredJobObservationAsync(Guid operation, Registration registration, CancellationToken ct)
    {
        var jobPath = RestoredJobPath(operation);
        RuntimeSourceStore.RequireNoLinks(jobPath);
        if (Path.Exists(jobPath)) return;

        var job = await ReadRestoredJobResultAsync(await ReadRestoredJobDispatchAsync(operation, ct), ct);
        var observed = await ObserveRestoredProcessAsync(operation, registration, ct);
        if (observed.Process.InvocationId != job.InvocationId) Fail("network_restored_start_unconfirmed");

        var processPath = RestoredProcessPath(operation);
        RuntimeSourceStore.RequireNoLinks(processPath);
        var process = JsonSerializer.Serialize(observed);
        // A cut between these writes can resume only with the same native process and original job.
        if (!Path.Exists(processPath))
            await WriteRestoreFileAsync(operation, processPath, process, UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        else if (await ReadRestoreFileAsync(processPath, ct) != process)
            Fail("network_restored_start_unconfirmed");
        await WriteRestoreFileAsync(operation, jobPath, JsonSerializer.Serialize(job),
            UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
    }

    private static async Task<RestoredProcess> ObserveRestoredProcessAsync(Guid operation, Registration registration, CancellationToken ct)
    {
        var state = await StateAsync(ct);
        var process = await ReadTunProcessAsync(state, ct);
        if (registration.TunOperation is not null || state.GetValueOrDefault("UnitFileState") != "disabled"
            || await VerifyProcessAsync(registration, state, ct) is not { } verified || verified.Pid != process.Pid
            || await ReadTunProcessAsync(await StateAsync(ct), ct) != process)
            Fail("network_restored_start_unconfirmed");
        return new(operation, registration.ConfigurationHash, process);
    }

    internal async Task VerifyRestoredTunProcessAsync(Guid operation, CancellationToken ct)
    {
        var path = RestoredProcessPath(operation);
        RuntimeSourceStore.RequireNoLinks(path);
        if (!File.Exists(path) || await ReadRestoreFileAsync(RestoredStartPath(operation), ct) != operation.ToString("N"))
            Fail("network_restored_start_unconfirmed");
        var registration = await ReadRegistrationAsync(ct) ?? throw new RuntimePolicyException("runtime.network_registration_conflict");
        var observed = await ObserveRestoredProcessAsync(operation, registration, ct);
        var job = JsonSerializer.Deserialize<RestoredStartJob>(await ReadRestoreFileAsync(RestoredJobPath(operation), ct));
        if (job is null || job.OperationId != operation || job.BootId != LinuxMihomoTunCheckpoint.BootId()
            || job.JobId == 0 || job.InvocationId != observed.Process.InvocationId
            || await ReadRestoreFileAsync(path, ct) != JsonSerializer.Serialize(observed))
            Fail("network_restored_start_unconfirmed");
        MihomoRuntimeData.SyncConfigurationDirectory(Path.GetDirectoryName(path)!);
    }

    private static async Task<string> ReadRestoreFileAsync(string path, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 8192) Fail("network_registration_conflict");
        RequireProtected(path);
        return Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct));
    }

    private static async Task WriteRestoreFileAsync(Guid operation, string path, string text, UnixFileMode mode, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        var temporary = ClearRestoreTemporary(operation, path);
        try
        {
            await using (var output = new FileStream(temporary,
                new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = mode }))
            {
                await output.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
            MihomoRuntimeData.SyncConfigurationDirectory(Path.GetDirectoryName(path)!);
        }
        finally { File.Delete(temporary); }
    }

    private static void SyncExistingRestoreFile(Guid operation, string path)
    {
        ClearRestoreTemporary(operation, path);
        MihomoRuntimeData.SyncConfigurationDirectory(Path.GetDirectoryName(path)!);
    }

    private static string ClearRestoreTemporary(Guid operation, string path)
    {
        var temporary = path + "." + operation.ToString("N") + ".restore";
        RuntimeSourceStore.RequireNoLinks(temporary);
        if (File.Exists(temporary))
        {
            RequireProtected(temporary);
            File.Delete(temporary);
        }
        return temporary;
    }
}
