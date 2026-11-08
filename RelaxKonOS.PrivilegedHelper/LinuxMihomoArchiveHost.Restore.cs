using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed partial class LinuxMihomoArchiveHost
{
    private sealed record RegistrationRestore(Guid OperationId, Registration Previous, Registration Restored);

    internal async Task<bool> RestoreTunRegistrationAsync(RuntimePendingConfiguration pending, string binary, string data,
        string version, bool resumeOnly, CancellationToken ct)
    {
        var operation = pending.OperationId;
        var directory = Path.Combine(Root, "operations", operation.ToString("N"));
        var path = Path.Combine(directory, "registration-restore.json");
        RuntimeSourceStore.RequireNoLinks(path);
        if (!Path.Exists(path) && resumeOnly) return false;

        var configuration = Root + "/active.yaml";
        var identity = pending.Plan.PreviousIdentity;
        ValidatePaths(binary, data, configuration, identity);
        RequireProtected(binary);
        RequireProtected(data);
        RequireProtected(configuration);
        if (pending.Plan.NetworkIntentIdentity is null || pending.Snapshot is not { BootEnabled: false, Running: true }
            || !string.Equals(await DigestAsync(configuration, ct), identity, StringComparison.OrdinalIgnoreCase))
            Fail("configuration_drift");
        var digest = await DigestAsync(binary, ct);
        var previous = new Registration(binary, data, digest, pending.Plan.CandidateIdentity, version, operation);
        var restored = previous with { TunOperation = null, ConfigurationHash = identity };
        var expected = JsonSerializer.Serialize(new RegistrationRestore(operation, previous, restored));
        var existing = Path.Exists(path);
        if (existing)
        {
            if (await ReadRestoreFileAsync(path, ct) != expected) Fail("network_registration_conflict");
            MihomoRuntimeData.SyncConfigurationDirectory(directory);
        }
        else if (await ReadRegistrationAsync(ct) != previous)
            Fail("network_registration_conflict");

        var registration = await ReadRestoreFileAsync(RegistrationPath, ct);
        var unit = await ReadRestoreFileAsync(UnitPath, ct);
        var previousRegistration = registration == JsonSerializer.Serialize(previous);
        var restoredRegistration = registration == JsonSerializer.Serialize(restored);
        var previousUnit = unit == UnitText(previous);
        var restoredUnit = unit == UnitText(restored);
        if (!(previousRegistration && previousUnit || restoredRegistration && (previousUnit || restoredUnit)))
            Fail("network_registration_conflict");

        var state = await StateAsync(ct, allowReload: existing);
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

        if (!existing)
            await WriteRestoreFileAsync(operation, path, expected, UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        if (previousRegistration)
            await WriteRestoreFileAsync(operation, RegistrationPath, JsonSerializer.Serialize(restored),
                UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
        else MihomoRuntimeData.SyncConfigurationDirectory(Root);
        if (previousUnit)
            await WriteRestoreFileAsync(operation, UnitPath, UnitText(restored),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ct);
        else MihomoRuntimeData.SyncConfigurationDirectory(Path.GetDirectoryName(UnitPath)!);
        if (previousUnit || state.GetValueOrDefault("NeedDaemonReload") == "yes")
            await CommandAsync("daemon-reload", ct, false);
        if ((await StateAsync(ct)).GetValueOrDefault("ActiveState") == "failed")
            await CommandAsync("reset-failed", ct);
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
        var temporary = path + "." + operation.ToString("N") + ".restore";
        RuntimeSourceStore.RequireNoLinks(temporary);
        if (File.Exists(temporary))
        {
            RequireProtected(temporary);
            File.Delete(temporary);
        }
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
}
