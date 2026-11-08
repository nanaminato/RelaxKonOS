using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed partial class LinuxMihomoArchiveHost
{
    // Recovery holds the original archive instance lock. A dispatched launch may leave a systemctl sender
    // alive after the broker exits; a successful, reaped sender and a settled unit are required in that case.
    internal async Task CompleteUndeliveredTunAsync(MihomoTunEffect effect, CancellationToken ct)
    {
        var routes = await RequireUndeliveredNetworkAsync(effect, ct);
        if (await ReadUndeliveredCheckpointAsync(effect, "network-before-launch-restored", ct))
        {
            await VerifyAbsentRuleAsync(effect, routes);
            return;
        }

        var (registration, _, current) = await ReadTunOperationAsync(effect.Intent.OperationId, ct);
        if (current != effect || registration.ConfigurationHash != effect.Intent.ConfigurationHash)
            Fail("network_checkpoint_conflict");
        var state = await StateAsync(ct);
        if (state.GetValueOrDefault("MainPID") != "0" || state.GetValueOrDefault("ActiveState") is not ("inactive" or "failed")
            || state.GetValueOrDefault("UnitFileState") != "disabled"
            || effect.Phase != MihomoTunPhase.NetworkDispatched && state.GetValueOrDefault("Job") is not ("" or "0"))
            Fail("network_stop_unconfirmed");

        var dispatched = await ReadUndeliveredCheckpointAsync(effect, "network-before-launch-removal-dispatched", ct);
        if (!dispatched && await routes.HasScopedRuleAsync())
        {
            if (!await ReadUndeliveredCheckpointAsync(effect with { Phase = MihomoTunPhase.NetworkDispatched, Process = null },
                "network-rule-verified", ct))
                Fail("network_rule_ownership_unknown");
            await routes.RequireRuleAsync(present: true);
            await routes.RequireManagementAsync(effect.Intent.ManagementHash, ownedRulePresent: true);
            await WriteUndeliveredCheckpointAsync(effect, "network-before-launch-removal-dispatched", ct);
            await routes.RemoveRuleAsync();
        }
        await VerifyAbsentRuleAsync(effect, routes);
        await WriteUndeliveredCheckpointAsync(effect, "network-before-launch-restored", ct);
    }

    internal static async Task VerifyUndeliveredTunRestoredAsync(MihomoTunEffect effect, CancellationToken ct)
    {
        var routes = await RequireUndeliveredNetworkAsync(effect, ct);
        if (!await ReadUndeliveredCheckpointAsync(effect, "network-before-launch-restored", ct))
            Fail("network_stop_unconfirmed");
        await VerifyAbsentRuleAsync(effect, routes);
    }

    private static async Task<LinuxMihomoTunRoutes> RequireUndeliveredNetworkAsync(MihomoTunEffect effect, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var intent = effect.Intent;
        if (!SupportsUndeliveredRecovery(effect.Phase)
            || intent.BootId != LinuxMihomoTunCheckpoint.BootId()
            || intent.NetworkNamespace != LinuxMihomoTunCheckpoint.NetworkNamespace())
            Fail("network_checkpoint_conflict");
        if (effect.Phase != MihomoTunPhase.NetworkDispatched
            && !await ReadUndeliveredCheckpointAsync(effect with { Phase = MihomoTunPhase.LaunchDispatched, Process = null },
                "launch-sender-exited", ct))
            Fail("network_launch_sender_unconfirmed");
        var routes = TunRoutes(intent);
        if (Directory.Exists($"/proc/{effect.BrokerPid}")
            || effect.Process is { } process && Directory.Exists($"/proc/{process.Pid}")
            || !await routes.IsAbsentAsync(intent.Device, effect.InterfaceIndex))
            Fail("network_stop_unconfirmed");
        return routes;
    }

    private static async Task VerifyAbsentRuleAsync(MihomoTunEffect effect, LinuxMihomoTunRoutes routes)
    {
        await routes.RequireRuleAsync(present: false);
        await routes.RequireManagementAsync(effect.Intent.ManagementHash);
    }

    private static string UndeliveredPath(MihomoTunEffect effect, string name) =>
        Path.Combine(Root, "operations", effect.Intent.OperationId.ToString("N"), name + ".json");

    private static async Task<bool> ReadUndeliveredCheckpointAsync(MihomoTunEffect effect, string name, CancellationToken ct)
    {
        var path = UndeliveredPath(effect, name);
        RuntimeSourceStore.RequireNoLinks(path);
        if (!Path.Exists(path)) return false;
        if (await ReadRestoreFileAsync(path, ct) != JsonSerializer.Serialize(effect)) Fail("network_checkpoint_conflict");
        MihomoRuntimeData.SyncConfigurationDirectory(Path.GetDirectoryName(path)!);
        return true;
    }

    private static async Task WriteUndeliveredCheckpointAsync(MihomoTunEffect effect, string name, CancellationToken ct)
    {
        var path = UndeliveredPath(effect, name);
        RuntimeSourceStore.RequireNoLinks(path);
        if (!SupportsUndeliveredRecovery(effect.Phase) || Path.Exists(path))
            Fail("network_checkpoint_conflict");
        await WriteRestoreFileAsync(effect.Intent.OperationId, path, JsonSerializer.Serialize(effect),
            UnixFileMode.UserRead | UnixFileMode.UserWrite, ct);
    }

    internal static bool SupportsUndeliveredRecovery(MihomoTunPhase phase) =>
        phase is MihomoTunPhase.NetworkDispatched or MihomoTunPhase.LaunchDispatched or MihomoTunPhase.DescriptorDispatched;
}
