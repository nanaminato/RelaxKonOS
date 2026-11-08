using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed partial class LinuxMihomoArchiveHost
{
    private sealed record RegistrationRestore(Guid OperationId, Registration Previous, Registration Restored);

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
                return true; // The caller verifies the existing process and credential, without dispatching start again.
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
