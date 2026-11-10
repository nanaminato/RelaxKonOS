using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.ManagedRuntimes;
using static RelaxKonOS.Server.Proxy.Mihomo.MihomoOfficialRuntimeState;

namespace RelaxKonOS.Server.Proxy.Mihomo;

internal sealed record MihomoRuntimeObservation(ProxyRuntimeDto Runtime, ControllerResult<bool>? Controller);

internal sealed class MihomoRuntimeStatusReader(IServiceScopeFactory scopes, MihomoOfficialRuntimeState official,
    MihomoRuntimePublicationReader? managedRuntime = null) : IProxyRuntimeManager
{
    public Task<ProxyRuntimeDto> GetAsync(string engineId, CancellationToken cancellationToken) =>
        GetCoreAsync(engineId, ObserveControllerAsync, cancellationToken);

    internal async Task<MihomoRuntimeObservation> GetObservedAsync(Func<CancellationToken, Task<ControllerResult<bool>>> observeController,
        CancellationToken cancellationToken)
    {
        ControllerResult<bool>? observation = null;
        var runtime = await GetCoreAsync(MihomoEngine.Id, ObserveAsync, cancellationToken);
        return new(runtime, observation);

        async Task<ControllerResult<bool>> ObserveAsync(CancellationToken ct) =>
            observation = await observeController(ct);
    }

    private async Task<ProxyRuntimeDto> GetCoreAsync(string engineId,
        Func<CancellationToken, Task<ControllerResult<bool>>> observeController, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id)
            return new(engineId, ProxyRuntimeMode.None, ProxyRuntimeState.Failed, null, null, false, false, ProxyProblemCodes.NotSupported);
        await official.Gate.WaitAsync(cancellationToken);
        try
        {
            var state = await official.ReadAsync(cancellationToken);
            // Installation, updates and removal belong to the managed-runtime ledger, so the
            // installed bytes are described by the Host's own observation rather than by a local
            // installation assumption. Only an answer the Helper actually produced
            // is projected; an unreadable observation stays unknown instead of becoming "not installed".
            if (OperatingSystem.IsWindows() && state?.ActiveVersion is not { Length: > 0 })
                return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false, ProxyProblemCodes.RuntimeNotInstalled);
            if (state?.ActiveVersion is not { Length: > 0 })
                return await ManagedProjectionAsync(state, observeController, cancellationToken);
            if (!File.Exists(official.ExecutablePath(state.ActiveVersion)))
                return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, state.ActiveVersion, state.PreviousVersion, false, false, ProxyProblemCodes.RuntimeNotInstalled);

            // Runtime status is intentionally read-only. Opening Proxy Manager or pressing its
            // ordinary refresh button must never rewrite configuration or restart Mihomo: a
            // restart can make provider-backed subscriptions fetch again. Managed
            // controller/GEO settings are applied by explicit configuration and subscription
            // refresh operations instead.
            var runtimeState = RunningState(await observeController(cancellationToken));
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, runtimeState, state.ActiveVersion, state.PreviousVersion, true, false);
        }
        finally { official.Gate.Release(); }
    }

    /// <summary>Maps the managed ledger's own installation facts onto the proxy runtime view.</summary>
    private async Task<ProxyRuntimeDto> ManagedProjectionAsync(RuntimeState? state,
        Func<CancellationToken, Task<ControllerResult<bool>>> observeController, CancellationToken cancellationToken)
    {
        if (managedRuntime is null)
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false, ProxyProblemCodes.RuntimeNotInstalled);
        RuntimeInstallationSnapshot snapshot;
        try { snapshot = await managedRuntime.ObserveAsync(cancellationToken); }
        catch (Exception ex) when (ex is RuntimeLifecycleException or RuntimePolicyException or IOException or HttpRequestException
            or OperationCanceledException or System.Text.Json.JsonException)
        {
            // An unmeasured Host is not an absent installation.
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Failed, null, state?.PreviousVersion, false, false,
                ProxyProblemCodes.RuntimeStateUnknown);
        }
        var installation = snapshot.Installation;
        var version = snapshot.MeasuredVersion;
        return installation.Ownership switch
        {
            RuntimeOwnership.Managed => new(MihomoEngine.Id, ProxyRuntimeMode.Managed,
                snapshot.ProblemCode is null ? RunningState(await observeController(cancellationToken)) : ProxyRuntimeState.Failed,
                version, state?.PreviousVersion, snapshot.ProblemCode is null, false, ProblemCode(snapshot.ProblemCode)),
            RuntimeOwnership.External => new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Stopped, version,
                null, false, true, ProblemCode(snapshot.ProblemCode)),
            RuntimeOwnership.Absent when snapshot.ProblemCode is null =>
                new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false, ProxyProblemCodes.RuntimeNotInstalled),
            // Ownership that could not be measured, or an installation that exists while its
            // owner is unproven, must never be reported as a clean "not installed".
            _ => new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Failed, version, null, false, false,
                ProblemCode(snapshot.ProblemCode))
        };
    }

    private async Task<ControllerResult<bool>> ObserveControllerAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var controller = scope.ServiceProvider.GetRequiredService<IMihomoControllerClient>();
        // An HTTP 401 proves Mihomo is listening even though its credential is wrong; the health
        // projection reports that mismatch separately.
        return await controller.IsReachableAsync(cancellationToken);
    }

    private static ProxyRuntimeState RunningState(ControllerResult<bool> controllerHealth) =>
        controllerHealth.Succeeded || controllerHealth.ProblemCode == ProxyProblemCodes.ControllerAuthenticationFailed
            ? ProxyRuntimeState.Running : ProxyRuntimeState.Stopped;

    /// <summary>Keeps the public code in the proxy namespace the clients already localize.</summary>
    private static string ProblemCode(string? code) => string.IsNullOrEmpty(code) ? "" : ProxyProblemCodes.RuntimeStateUnknown;
}
