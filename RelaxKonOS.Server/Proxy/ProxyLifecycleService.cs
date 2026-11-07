using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy;

public interface IProxyLifecycleService
{
    Task<string?> ExecuteLifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken);
}

/// <summary>Engine-neutral lifecycle coordinator.  OS service actions remain constrained by IProxyPrivilegedOperations.</summary>
public sealed class ProxyLifecycleService(
    IProxyRuntimeManager runtime,
    IProxyPrivilegedOperations privileged) : IProxyLifecycleService
{
    private const string ServiceName = "relaxkonos-mihomo";

    public async Task<string?> ExecuteLifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken)
    {
        var installed = await runtime.GetAsync(MihomoEngine.Id, cancellationToken);
        if (installed.State == ProxyRuntimeState.NotInstalled) return ProxyProblemCodes.RuntimeNotInstalled;
        var result = action switch
        {
            ProxyLifecycleAction.Start => await privileged.StartServiceAsync(new(MihomoEngine.Id, ServiceName), cancellationToken),
            ProxyLifecycleAction.Stop => await privileged.StopServiceAsync(new(MihomoEngine.Id, ServiceName), cancellationToken),
            ProxyLifecycleAction.Restart => await privileged.RestartServiceAsync(new(MihomoEngine.Id, ServiceName), cancellationToken),
            _ => new ProxyPrivilegedResult(false, ProxyProblemCodes.NotSupported),
        };
        return result.Succeeded ? null : result.ProblemCode;
    }
}
