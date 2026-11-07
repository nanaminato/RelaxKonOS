using System.Net;
using System.Net.Sockets;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Explicit recovery of the previous direct settings under the existing publication authority.</summary>
public sealed class MihomoDirectSettingsCompensationService(IProxyPlatformPaths paths, MihomoPublicationStore store,
    MihomoSettingsPublicationFiles files, IMihomoControllerClient controller, IProxyControllerSecretStore secrets)
{
    public async Task<RuntimeSwitchResult> CompensateAsync(Guid operationId, bool confirmed, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.request_invalid");
        if (!confirmed) throw new RuntimeLifecycleException("runtime.recovery_confirmation_required");
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.DirectCompensationReceipt(operationId) is { } receipt) return receipt;
            if (state.DirectReload is not { } direct) throw new RuntimeLifecycleException("runtime.recovery_absent");
            if (direct.OperationId != operationId) throw new RuntimeLifecycleException("runtime.recovery_target_conflict");
            if (!direct.CanCompensate || !files.SupportsTrackedHostPolicy)
                throw new RuntimeLifecycleException("runtime.direct_compensation_unavailable");
            if (direct.Compensation is { Stage: MihomoDirectCompensationStage.ReloadDispatched } uncertain)
                return Pending(uncertain.Id);

            var intent = direct.ConfirmedSettings!;
            await files.VerifyAsync(intent, allowCandidate: false, ct);
            var previous = direct.PreviousYaml!;
            if (MihomoManagedConfiguration.ReadTunActivation(previous).Enabled
                || await ProxyTunSafetyService.HasRecoveryMarkerAsync(paths, ct)
                || string.IsNullOrEmpty(await secrets.ReadExistingAsync(ct))
                || await controller.GetTunEnabledAsync(ct) is not { Succeeded: true, Value: false })
                throw new RuntimeLifecycleException("runtime.direct_compensation_unavailable");

            if (direct.Compensation is null)
            {
                await VerifyYamlAsync(direct.CandidateIdentity, ct);
                ct.ThrowIfCancellationRequested();
                state = await store.AdvanceDirectCompensationUnderGateAsync(state, MihomoDirectCompensationStage.ReloadDispatched);
                direct = state.DirectReload!;
                await store.RestoreDirectCompensationConfigurationUnderGateAsync(direct);
                if (await controller.ReloadAsync(CancellationToken.None) is not null)
                    return Pending(direct.Compensation!.Id);
                state = await store.AdvanceDirectCompensationUnderGateAsync(state, MihomoDirectCompensationStage.ReloadConfirmed);
                direct = state.DirectReload!;
            }

            var id = direct.Compensation!.Id;
            var previousIdentity = MihomoPublicationStore.Identity(previous);
            await VerifyYamlAsync(previousIdentity, CancellationToken.None);
            if (!await VerifyListenerAsync(intent.Previous)) return Pending(id);
            var hostRestore = intent with { Candidate = intent.Previous };
            if (direct.Compensation.Stage == MihomoDirectCompensationStage.ReloadConfirmed)
            {
                state = await store.AdvanceDirectCompensationUnderGateAsync(state, MihomoDirectCompensationStage.HostDispatched);
                if (!await files.ApplyTrackedAsync(id, hostRestore, CancellationToken.None)) return Pending(id);
            }
            if (!await files.MatchesTrackedEffectAsync(id, hostRestore, CancellationToken.None)) return Pending(id);
            await files.VerifyAsync(intent, allowCandidate: false, CancellationToken.None);
            await VerifyYamlAsync(previousIdentity, CancellationToken.None);
            if (!await VerifyListenerAsync(intent.Previous)) return Pending(id);
            return await store.CompleteDirectCompensationUnderGateAsync(state);
        }
        catch (RuntimePolicyException ex) { throw new RuntimeLifecycleException(ex.ProblemCode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ProxyControllerSecretException)
        { throw new RuntimeLifecycleException("runtime.publication_recovery_failed"); }
        finally { store.Gate.Release(); }

        RuntimeSwitchResult Pending(Guid id) => new(operationId, RuntimeSwitchOutcome.RecoveryRequired,
            MihomoPublicationStore.DirectCompensationPending, id);
    }

    private async Task VerifyYamlAsync(string expected, CancellationToken ct)
    {
        var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        RuntimeSourceStore.RequireNoLinks(active);
        if (!File.Exists(active) || MihomoPublicationStore.Identity(await File.ReadAllTextAsync(active, ct)) != expected)
            throw new RuntimePolicyException("runtime.configuration_drift");
    }

    private async Task<bool> VerifyListenerAsync(RelaxKonOS.Protocol.Proxy.ProxySettingsDto previous)
    {
        if (await controller.GetTunEnabledAsync(CancellationToken.None) is not { Succeeded: true, Value: false }) return false;
        var observed = await controller.GetMixedPortAsync(CancellationToken.None);
        if (!observed.Succeeded || observed.Value != previous.MixedPort) return false;
        var address = previous.SystemProxyEnabled ? IPAddress.Parse(previous.SystemProxyHost) : IPAddress.Loopback;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var socket = new TcpClient(address.AddressFamily);
        try { await socket.ConnectAsync(address, previous.MixedPort, budget.Token); return true; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }
}
