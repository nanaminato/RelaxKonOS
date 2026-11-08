using System.Net;
using System.Net.Sockets;
using System.Globalization;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
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
            var intent = direct.ConfirmedSettings!;
            await files.VerifyAsync(intent, allowCandidate: false, ct);
            var previous = direct.PreviousYaml!;
            var previousIdentity = MihomoPublicationStore.Identity(previous);
            var observableDelta = HasObservableListenerDelta(previous, direct.CandidateIdentity, intent);
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
            else if (direct.Compensation is { Stage: MihomoDirectCompensationStage.ReloadDispatched } uncertain)
            {
                if (!observableDelta || !await MatchesYamlAsync(previousIdentity, ct) || !await VerifyEffectAsync(ct))
                    return Pending(uncertain.Id);
                await VerifyYamlAsync(previousIdentity, ct);
                ct.ThrowIfCancellationRequested();
                state = await store.AdvanceDirectCompensationUnderGateAsync(state, MihomoDirectCompensationStage.ReloadConfirmed);
                direct = state.DirectReload!;
            }

            var id = direct.Compensation!.Id;
            await VerifyYamlAsync(previousIdentity, CancellationToken.None);
            if (!await VerifyEffectAsync(CancellationToken.None)) return Pending(id);
            await files.VerifyAsync(intent, allowCandidate: false, CancellationToken.None);
            var hostRestore = intent with { Candidate = intent.Previous };
            if (direct.Compensation.Stage == MihomoDirectCompensationStage.ReloadConfirmed)
            {
                state = await store.AdvanceDirectCompensationUnderGateAsync(state, MihomoDirectCompensationStage.HostDispatched);
                if (!await files.ApplyTrackedAsync(id, hostRestore, CancellationToken.None)) return Pending(id);
            }
            if (!await files.MatchesTrackedEffectAsync(id, hostRestore, CancellationToken.None)) return Pending(id);
            await files.VerifyAsync(intent, allowCandidate: false, CancellationToken.None);
            await VerifyYamlAsync(previousIdentity, CancellationToken.None);
            if (!await VerifyEffectAsync(CancellationToken.None)) return Pending(id);
            return await store.CompleteDirectCompensationUnderGateAsync(state);

            async Task<bool> VerifyEffectAsync(CancellationToken token) =>
                await VerifyListenerAsync(intent.Previous, token, requireObservedEffect: observableDelta)
                && (!observableDelta || await ObserveListenerAsync(intent.Candidate, token) == false);
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
        if (!await MatchesYamlAsync(expected, ct)) throw new RuntimePolicyException("runtime.configuration_drift");
    }

    private async Task<bool> MatchesYamlAsync(string expected, CancellationToken ct)
    {
        var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        RuntimeSourceStore.RequireNoLinks(active);
        return File.Exists(active) && MihomoPublicationStore.Identity(await File.ReadAllTextAsync(active, ct)) == expected;
    }

    // The controller exposes the port and log level, not the loaded YAML identity. Recreate
    // the frozen candidate digest using only those observable changes before trusting the effect.
    internal static bool HasObservableListenerDelta(string previous, string candidateIdentity, MihomoSettingsPublication intent)
    {
        if (intent.Previous.MixedPort == intent.Candidate.MixedPort
            || (intent.Previous with { MixedPort = intent.Candidate.MixedPort, LogLevel = intent.Candidate.LogLevel }) != intent.Candidate)
            return false;
        var candidate = ReplaceOwnedScalar(previous, "mixed-port", intent.Previous.MixedPort.ToString(CultureInfo.InvariantCulture),
            intent.Candidate.MixedPort.ToString(CultureInfo.InvariantCulture));
        if (candidate is null) return false;
        candidate = ReplaceOwnedScalar(candidate, "log-level", intent.Previous.LogLevel, intent.Candidate.LogLevel);
        return candidate is not null && MihomoPublicationStore.Identity(candidate) == candidateIdentity;
    }

    private static string? ReplaceOwnedScalar(string yaml, string key, string previous, string candidate)
    {
        var source = $"\n{key}: {previous}\n";
        var offset = yaml.IndexOf(source, StringComparison.Ordinal);
        if (offset < 0 || yaml.IndexOf(source, offset + source.Length - 1, StringComparison.Ordinal) >= 0) return null;
        return string.Concat(yaml.AsSpan(0, offset), $"\n{key}: {candidate}\n", yaml.AsSpan(offset + source.Length));
    }

    private async Task<bool> VerifyListenerAsync(ProxySettingsDto previous, CancellationToken ct, bool requireObservedEffect)
    {
        var observed = await controller.GetListenerStateAsync(ct);
        return observed is { Succeeded: true, Value: { TunEnabled: false } listener }
            && listener.MixedPort == previous.MixedPort && (!requireObservedEffect || listener.LogLevel == previous.LogLevel)
            && await ObserveListenerAsync(previous, ct, requireSocks: requireObservedEffect) == true;
    }

    private static async Task<bool?> ObserveListenerAsync(ProxySettingsDto settings, CancellationToken ct, bool requireSocks = false)
    {
        var address = settings.SystemProxyEnabled ? IPAddress.Parse(settings.SystemProxyHost) : IPAddress.Loopback;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        using var socket = new TcpClient(address.AddressFamily);
        try
        {
            await socket.ConnectAsync(address, settings.MixedPort, budget.Token);
            if (!requireSocks) return true;
            using var stream = socket.GetStream();
            // Negotiate the protocol without connecting through the proxy or sending credentials.
            await stream.WriteAsync(new byte[] { 5, 2, 0, 2 }, budget.Token);
            var response = new byte[2];
            await stream.ReadExactlyAsync(response, budget.Token);
            return response is [5, 0 or 2];
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return false; }
        catch (SocketException) { return null; }
        catch (IOException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
