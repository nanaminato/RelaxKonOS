using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed partial class LinuxMihomoArchiveHost
{
    // Activation and recovery callers hold the same archive instance lock.
    internal async Task<MihomoTunEffect> ActivateTunAsync(MihomoTunIntent intent, string binary, string data, CancellationToken ct,
        RuntimePendingConfiguration? publication = null)
    {
        ValidatePaths(binary, data, Root + "/active.yaml", intent.ConfigurationHash);
        if (intent.BootId != LinuxMihomoTunCheckpoint.BootId()
            || intent.NetworkNamespace != LinuxMihomoTunCheckpoint.NetworkNamespace())
            Fail("network_checkpoint_conflict");
        RequirePidFdSupport();
        var checkpoint = TunCheckpoint(binary, intent.OperationId, intent.ConfigurationHash);
        var routes = TunRoutes(intent);
        await routes.RequireManagementAsync(intent.ManagementHash);
        ct.ThrowIfCancellationRequested();
        var initial = new MihomoTunEffect(MihomoTunPhase.Intent, intent);
        await checkpoint.AppendAsync(initial);
        if (publication is not null)
            await ConfigurePublicationTunAsync(publication, binary, data, ct);
        using var descriptor = LinuxMihomoTunDescriptor.Create(intent.OperationId, intent.Device);
        await checkpoint.AppendAsync(initial with
        {
            Phase = MihomoTunPhase.NetworkDispatched,
            InterfaceIndex = descriptor.InterfaceIndex,
            BrokerPid = Environment.ProcessId
        });
        await routes.ApplyAsync(descriptor);
        await routes.RequireManagementAsync(intent.ManagementHash, ownedRulePresent: true);
        if (publication is not null)
        {
            var applied = checkpoint.Read() ?? throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
            await WriteUndeliveredCheckpointAsync(applied, "network-rule-verified", CancellationToken.None);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(checkpoint.SocketPath));
        File.SetUnixFileMode(checkpoint.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(1);
        if (publication is null)
            await ConfigureTunAsync(binary, data, intent.ConfigurationHash, intent.OperationId, CancellationToken.None);
        else
        {
            RequireProtected(TunLauncher);
            RequireProtected(checkpoint.SocketPath);
        }
        await DispatchTunStartAsync(intent.OperationId, CancellationToken.None, recordSenderExit: publication is not null);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var connection = await listener.AcceptAsync(deadline.Token);
        var process = await ReadTunProcessAsync(await StateAsync(deadline.Token), deadline.Token);
        var peer = new LinuxPeerAuthorizer([0]).Authorize(connection);
        if (!peer.Allowed || peer.UserId != 0 || peer.ProcessId != process.Pid
            || new FileInfo($"/proc/{process.Pid}/exe").LinkTarget != TunLauncher)
            Fail("network_recipient_mismatch");
        if (checkpoint.Read() is not { Phase: MihomoTunPhase.LaunchDispatched } launched)
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        var dispatched = launched with { Phase = MihomoTunPhase.DescriptorDispatched, Process = process };
        await checkpoint.AppendAsync(dispatched);
        descriptor.SendTo(connection, intent.OperationId, process.Pid, 0);
        await WaitAsync(state => state.GetValueOrDefault("ActiveState") is "inactive" or "failed"
            || new FileInfo($"/proc/{process.Pid}/exe").LinkTarget == binary, deadline.Token);
        var (registration, _, _) = await ReadTunOperationAsync(intent.OperationId, deadline.Token);
        await VerifyTunEffectAsync(registration, dispatched, deadline.Token);
        if (intent.DnsRoutingDomain is not null)
        {
            var dns = TunDns(dispatched);
            var baseline = await dns.CaptureAsync();
            dispatched = dispatched with { Phase = MihomoTunPhase.DnsDispatched, Dns = baseline };
            await checkpoint.AppendAsync(dispatched);
            await dns.ApplyAsync(baseline);
            dispatched = dispatched with { Phase = MihomoTunPhase.DnsApplied };
            await checkpoint.AppendAsync(dispatched);
        }
        return dispatched;
    }

    internal static async Task VerifyUndispatchedTunNetworkAsync(MihomoTunEffect effect, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var intent = effect.Intent;
        if (effect.Phase != MihomoTunPhase.Intent || intent.BootId != LinuxMihomoTunCheckpoint.BootId()
            || intent.NetworkNamespace != LinuxMihomoTunCheckpoint.NetworkNamespace())
            Fail("network_checkpoint_conflict");
        var routes = TunRoutes(intent);
        if (!await routes.IsAbsentAsync(intent.Device, 0)) Fail("network_stop_unconfirmed");
        await routes.RequireRuleAsync(present: false);
        await routes.RequireManagementAsync(intent.ManagementHash);
    }

    internal async Task<MihomoTunEffect> VerifyTunAsync(Guid operation, CancellationToken ct)
    {
        var (registration, _, effect) = await ReadTunOperationAsync(operation, ct);
        await VerifyActiveTunAsync(registration, effect, ct);
        return effect;
    }

    internal async Task<RuntimeMihomoTunOwnership> ObserveTunOwnershipAsync(Guid operation, RuntimeInstallation installation,
        CancellationToken ct)
    {
        var effect = await VerifyTunAsync(operation, ct);
        return TunOwnership(effect, installation);
    }

    internal async Task<bool> VerifyCommittedTunAsync(RuntimeMihomoTunOwnership ownership, RuntimeInstallation installation,
        Guid executionId, CancellationToken ct)
    {
        var (registration, _, effect) = await ReadCommittedTunAsync(ownership, installation, executionId, ct);
        if (effect.Phase == MihomoTunPhase.Restored)
        {
            await VerifyStoppedTunAsync(effect, ct);
            return false;
        }
        if (effect.Phase is MihomoTunPhase.StopDispatched or MihomoTunPhase.RuleRemovalDispatched)
            Fail("network_stop_unconfirmed");
        await VerifyActiveTunAsync(registration, effect, ct);
        return true;
    }

    // The caller holds the archive instance lock for the entire stop, including the final observation.
    internal async Task StopCommittedTunAsync(RuntimeMihomoTunOwnership ownership, RuntimeInstallation installation,
        Guid executionId, RuntimeStageReporter? progress, CancellationToken ct, bool allowDispatch = true)
    {
        var (_, _, effect) = await ReadCommittedTunAsync(ownership, installation, executionId, ct);
        if (effect.Phase is not (MihomoTunPhase.StopDispatched or MihomoTunPhase.RuleRemovalDispatched or MihomoTunPhase.Restored))
        {
            if (!allowDispatch) Fail("network_stop_unconfirmed");
            await DispatchTunStopAsync(executionId, ct);
            if (progress is not null)
            {
                try { await progress(InstallationStage.Verifying, false); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }
        await CompleteTunStopAsync(executionId, CancellationToken.None);
        await VerifyCommittedTunAsync(ownership, installation, executionId, CancellationToken.None);
    }

    private static async Task<(Registration, LinuxMihomoTunCheckpoint, MihomoTunEffect)> ReadCommittedTunAsync(
        RuntimeMihomoTunOwnership ownership, RuntimeInstallation installation, Guid executionId, CancellationToken ct)
    {
        var (registration, checkpoint, effect) = await ReadTunOperationAsync(executionId, ct);
        if (TunOwnership(effect, installation, ownership.OperationId) != ownership) Fail("network_checkpoint_conflict");
        RequireProtected(Root + "/active.yaml");
        if (MihomoArchiveAdapter.Hash(await MihomoArchiveAdapter.ReadConfigurationAsync(Root + "/active.yaml", ct))
            != ownership.ConfigurationIdentity) Fail("configuration_drift");
        return (registration, checkpoint, effect);
    }

    internal async Task<MihomoTunIntent> ReadCommittedTunIntentAsync(RuntimeMihomoTunOwnership ownership,
        RuntimeInstallation installation, Guid previousExecution, bool stopped, CancellationToken ct)
    {
        var (registration, _, effect) = await ReadCommittedTunAsync(ownership, installation, previousExecution, ct);
        if (effect.Phase == MihomoTunPhase.Restored) await VerifyStoppedTunAsync(effect, ct);
        else if (stopped) Fail("network_stop_unconfirmed");
        else await VerifyActiveTunAsync(registration, effect, ct);
        return effect.Intent;
    }

    internal async Task UnregisterCommittedTunAsync(RuntimeMihomoTunOwnership owner, RuntimeInstallation installation,
        Guid executionId, CancellationToken ct)
    {
        await ReadCommittedTunIntentAsync(owner, installation, executionId, stopped: true, ct);
        File.Delete(UnitPath);
        File.Delete(RegistrationPath);
        LinuxDirectorySync.Flush(Path.GetDirectoryName(UnitPath)!);
        LinuxDirectorySync.Flush(Root);
        await CommandAsync("daemon-reload", ct, unit: false);
    }

    internal async Task VerifyUnregisteredTunAsync(RuntimeMihomoTunOwnership owner, RuntimeInstallation installation,
        Guid executionId, CancellationToken ct)
    {
        if (Path.Exists(UnitPath) || Path.Exists(RegistrationPath) || (await ObserveAsync(ct)).Exists)
            Fail("network_removal_unconfirmed");
        var checkpoint = new LinuxMihomoTunCheckpoint(Root, executionId, owner.RevisionId, owner.ConfigurationIdentity);
        if (checkpoint.Read() is not { } effect || TunOwnership(effect, installation, owner.OperationId) != owner)
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        await VerifyRestoredTunNetworkAsync(effect, ct);
    }

    internal static async Task VerifyRestoredTunNetworkAsync(MihomoTunEffect effect, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (effect.Phase != MihomoTunPhase.Restored || effect.Intent.BootId != LinuxMihomoTunCheckpoint.BootId()
            || effect.Intent.NetworkNamespace != LinuxMihomoTunCheckpoint.NetworkNamespace())
            Fail("network_checkpoint_conflict");
        var routes = TunRoutes(effect.Intent);
        if (effect.Process is not { } process || Directory.Exists($"/proc/{process.Pid}")
            || !await routes.IsAbsentAsync(effect.Intent.Device, effect.InterfaceIndex)) Fail("network_stop_unconfirmed");
        await VerifyStoppedTunNetworkAsync(effect, routes);
    }

    internal async Task VerifyCommittedLaunchAsync(RuntimeMihomoTunOwnership ownership, RuntimeInstallation installation,
        Guid executionId, MihomoPublishedRuntime material, CancellationToken ct)
    {
        var (registration, _, effect) = await ReadCommittedTunAsync(ownership, installation, executionId, ct);
        if (effect.Phase == MihomoTunPhase.DnsDispatched)
        {
            await ReconcileTunDnsAsync(executionId, ct);
            (_, _, effect) = await ReadCommittedTunAsync(ownership, installation, executionId, ct);
        }
        await VerifyActiveTunAsync(registration, effect, ct);
        MihomoConfigurationPolicy.RequireDescriptorTun(material, effect.Intent.Device);
        if (!await ListenersHealthyAsync(material, ct)) Fail("health_check_failed");
    }

    private static async Task VerifyStoppedTunAsync(MihomoTunEffect effect, CancellationToken ct)
    {
        var routes = TunRoutes(effect.Intent);
        if (!await HasStoppedTunAsync(effect, routes, ct)) Fail("network_stop_unconfirmed");
        await VerifyStoppedTunNetworkAsync(effect, routes);
    }

    private static async Task VerifyStoppedTunNetworkAsync(MihomoTunEffect effect, LinuxMihomoTunRoutes routes)
    {
        if (effect.Dns is { } dns) await TunDns(effect).VerifyAsync(dns, present: false);
        await routes.RequireRuleAsync(present: false);
        await routes.RequireManagementAsync(effect.Intent.ManagementHash);
    }

    internal static MihomoTunNetworkIntent TunNetwork(MihomoTunIntent intent) =>
        new(intent.Device, intent.Address, intent.Destination, intent.ManagementAddress,
            intent.ManagementUserId, intent.ManagementHash, intent.DnsRoutingDomain);

    private static RuntimeMihomoTunOwnership TunOwnership(MihomoTunEffect effect, RuntimeInstallation installation,
        Guid? publicationId = null)
    {
        var intent = effect.Intent;
        var operation = publicationId ?? intent.OperationId;
        var identity = MihomoRuntimeMaterial.ConfigurationTunIntentIdentity(operation, installation.InstallationId,
            installation.HostId, intent.RevisionId, intent.ConfigurationHash, TunNetwork(intent));
        return new(operation, intent.RevisionId, intent.ConfigurationHash, identity);
    }

    internal async Task ReconcileTunDnsAsync(Guid operation, CancellationToken ct)
    {
        var (registration, checkpoint, effect) = await ReadTunOperationAsync(operation, ct);
        if (effect is not { Phase: MihomoTunPhase.DnsDispatched, Dns: { } dns })
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        await VerifyTunEffectAsync(registration, effect, ct);
        await TunDns(effect).VerifyAsync(dns, present: true);
        await checkpoint.AppendAsync(effect with { Phase = MihomoTunPhase.DnsApplied });
    }

    internal async Task DispatchTunStopAsync(Guid operation, CancellationToken ct)
    {
        var (registration, checkpoint, effect) = await ReadTunOperationAsync(operation, ct);
        RequireActiveTunPhase(effect);
        if (effect.Process is not { } process)
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");

        // Pin the kernel process before verifying it, so an intervening exit cannot redirect the signal.
        using var handle = new SafeFileHandle(OpenPidFd(process.Pid, 0), ownsHandle: true);
        if (handle.IsInvalid) Fail("network_stop_unconfirmed");
        await VerifyTunEffectAsync(registration, effect, ct);
        if (effect.Dns is { } dns) await TunDns(effect).VerifyAsync(dns, present: true);
        ct.ThrowIfCancellationRequested();
        await checkpoint.AppendAsync(effect with { Phase = MihomoTunPhase.StopDispatched });
        const int sigterm = 15;
        if (SignalPidFd(handle, sigterm, 0, 0) != 0) Fail("network_stop_unconfirmed");
    }

    internal async Task CompleteTunStopAsync(Guid operation, CancellationToken ct)
    {
        var (_, checkpoint, effect) = await ReadTunOperationAsync(operation, ct);
        if (effect.Phase == MihomoTunPhase.Restored) return;
        if (effect.Phase is not (MihomoTunPhase.StopDispatched or MihomoTunPhase.RuleRemovalDispatched))
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        var routes = TunRoutes(effect.Intent);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await HasStoppedTunAsync(effect, routes, ct))
            {
                if (effect.Dns is { } dns) await TunDns(effect).VerifyAsync(dns, present: false);
                if (effect.Phase == MihomoTunPhase.StopDispatched)
                {
                    await routes.RequireManagementAsync(effect.Intent.ManagementHash, ownedRulePresent: true);
                    effect = effect with { Phase = MihomoTunPhase.RuleRemovalDispatched };
                    await checkpoint.AppendAsync(effect);
                    await routes.RemoveRuleAsync();
                }
                await routes.RequireRuleAsync(present: false);
                await routes.RequireManagementAsync(effect.Intent.ManagementHash);
                await checkpoint.AppendAsync(effect with { Phase = MihomoTunPhase.Restored });
                return;
            }
            await Task.Delay(100, ct);
        }
        Fail("network_stop_unconfirmed");
    }

    private static async Task<bool> HasStoppedTunAsync(MihomoTunEffect effect, LinuxMihomoTunRoutes routes, CancellationToken ct)
    {
        var state = await StateAsync(ct);
        return state.GetValueOrDefault("MainPID") == "0"
            && state.GetValueOrDefault("ActiveState") is "inactive" or "failed"
            && state.GetValueOrDefault("UnitFileState") == "disabled"
            && effect.Process is { } process && !Directory.Exists($"/proc/{process.Pid}")
            && await routes.IsAbsentAsync(effect.Intent.Device, effect.InterfaceIndex);
    }

    private static async Task VerifyActiveTunAsync(Registration registration, MihomoTunEffect effect, CancellationToken ct)
    {
        RequireActiveTunPhase(effect);
        await VerifyTunEffectAsync(registration, effect, ct);
        if (effect.Dns is { } dns) await TunDns(effect).VerifyAsync(dns, present: true);
    }

    private static async Task<(Registration, LinuxMihomoTunCheckpoint, MihomoTunEffect)> ReadTunOperationAsync(Guid operation, CancellationToken ct)
    {
        var registration = await ReadRegistrationAsync(ct);
        if (registration is null || registration.TunOperation != operation) Fail("network_checkpoint_conflict");
        var checkpoint = TunCheckpoint(registration.Binary, operation, registration.ConfigurationHash);
        var effect = checkpoint.Read();
        if (effect is null || effect.Intent.BootId != LinuxMihomoTunCheckpoint.BootId()
            || effect.Intent.NetworkNamespace != LinuxMihomoTunCheckpoint.NetworkNamespace())
            Fail("network_checkpoint_conflict");
        return (registration, checkpoint, effect);
    }

    private static async Task VerifyTunEffectAsync(Registration registration, MihomoTunEffect effect, CancellationToken ct)
    {
        var state = await StateAsync(ct);
        if (state.GetValueOrDefault("UnitFileState") != "disabled"
            || await ReadTunProcessAsync(state, ct) != effect.Process
            || await VerifyProcessAsync(registration, state, ct) is not { } verified)
            throw new RuntimePolicyException("runtime.network_recipient_mismatch");
        var proc = $"/proc/{verified.Pid}";
        if (new FileInfo(proc + "/ns/net").LinkTarget != effect.Intent.NetworkNamespace
            || !(await File.ReadAllLinesAsync($"{proc}/fdinfo/{TunFd}", ct)).Contains($"iff:\t{effect.Intent.Device}"))
            Fail("network_device_identity_unknown");
        var routes = TunRoutes(effect.Intent);
        await routes.VerifyAsync(effect.Intent.OperationId, effect.Intent.Device, effect.InterfaceIndex);
        await routes.RequireManagementAsync(effect.Intent.ManagementHash, ownedRulePresent: true);
        if (await ReadTunProcessAsync(await StateAsync(ct), ct) != effect.Process) Fail("network_recipient_mismatch");
    }

    private static async Task<MihomoTunProcess> ReadTunProcessAsync(Dictionary<string, string> state, CancellationToken ct)
    {
        if (!int.TryParse(state.GetValueOrDefault("MainPID"), CultureInfo.InvariantCulture, out var pid) || pid <= 1
            || !Guid.TryParseExact(state.GetValueOrDefault("InvocationID"), "N", out var invocation) || invocation == Guid.Empty)
            throw new RuntimePolicyException("runtime.network_recipient_mismatch");
        var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat", ct);
        var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], CultureInfo.InvariantCulture, out var ticks) || ticks == 0)
            throw new RuntimePolicyException("runtime.network_recipient_mismatch");
        return new(pid, invocation, ticks);
    }

    private static LinuxMihomoTunRoutes TunRoutes(MihomoTunIntent intent) =>
        new(IPAddress.Parse(intent.Address), IPAddress.Parse(intent.Destination), IPAddress.Parse(intent.ManagementAddress),
            intent.ManagementUserId, intent.RoutingTable);

    private static LinuxMihomoTunDns TunDns(MihomoTunEffect effect) =>
        new(effect.Intent.DnsRoutingDomain!, IPAddress.Parse(effect.Intent.Destination), effect.InterfaceIndex);

    private static void RequireActiveTunPhase(MihomoTunEffect effect)
    {
        var expected = effect.Intent.DnsRoutingDomain is null ? MihomoTunPhase.DescriptorDispatched : MihomoTunPhase.DnsApplied;
        if (effect.Phase != expected) Fail("network_checkpoint_conflict");
    }

    private static void RequirePidFdSupport()
    {
        try
        {
            using var process = new SafeFileHandle(OpenPidFd(Environment.ProcessId, 0), ownsHandle: true);
            // Signal zero checks availability without delivering a signal.
            if (process.IsInvalid || SignalPidFd(process, 0, 0, 0) != 0) Fail("network_platform_unsupported");
        }
        catch (EntryPointNotFoundException) { Fail("network_platform_unsupported"); }
    }

    [DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    private static extern int OpenPidFd(int pid, uint flags);

    [DllImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    private static extern int SignalPidFd(SafeFileHandle descriptor, int signal, nint info, uint flags);
}
