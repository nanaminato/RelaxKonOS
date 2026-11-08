using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed partial class LinuxMihomoArchiveHost
{
    // Activation and recovery callers hold the same archive instance lock.
    internal async Task<MihomoTunEffect> ActivateTunAsync(MihomoTunIntent intent, string binary, string data, CancellationToken ct)
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
        using var descriptor = LinuxMihomoTunDescriptor.Create(intent.OperationId, intent.Device);
        await checkpoint.AppendAsync(initial with
        {
            Phase = MihomoTunPhase.NetworkDispatched,
            InterfaceIndex = descriptor.InterfaceIndex,
            BrokerPid = Environment.ProcessId
        });
        await routes.ApplyAsync(descriptor);
        await routes.RequireManagementAsync(intent.ManagementHash, ownedRulePresent: true);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(checkpoint.SocketPath));
        File.SetUnixFileMode(checkpoint.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(1);
        await ConfigureTunAsync(binary, data, intent.ConfigurationHash, intent.OperationId, CancellationToken.None);
        await DispatchTunStartAsync(intent.OperationId, CancellationToken.None);

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

    internal async Task VerifyTunAsync(Guid operation, CancellationToken ct)
    {
        var (registration, _, effect) = await ReadTunOperationAsync(operation, ct);
        RequireActiveTunPhase(effect);
        await VerifyTunEffectAsync(registration, effect, ct);
        if (effect.Dns is { } dns) await TunDns(effect).VerifyAsync(dns, present: true);
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
        if (effect.Process is not { } process
            || effect.Phase is not (MihomoTunPhase.StopDispatched or MihomoTunPhase.RuleRemovalDispatched))
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        var routes = TunRoutes(effect.Intent);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var state = await StateAsync(ct);
            if (state.GetValueOrDefault("MainPID") == "0"
                && state.GetValueOrDefault("ActiveState") is "inactive" or "failed"
                && !Directory.Exists($"/proc/{process.Pid}")
                && await routes.IsAbsentAsync(effect.Intent.Device, effect.InterfaceIndex))
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
