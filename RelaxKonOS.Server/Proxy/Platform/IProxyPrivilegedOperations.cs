using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Platform;

/// <summary>
/// The entire privileged Proxy surface. Requests use opaque IDs and paths resolved by the Server;
/// clients can never submit a command, executable, arguments, password, or environment variables.
/// </summary>
public interface IProxyPrivilegedOperations
{
    Task<ProxyPrivilegedResult> InstallRuntimeAsync(InstallProxyRuntimeOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> RemoveRuntimeAsync(RemoveProxyRuntimeOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> ReplaceRuntimeAsync(ReplaceProxyRuntimeOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> InstallServiceAsync(InstallProxyServiceOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> RemoveServiceAsync(RemoveProxyServiceOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> SetServiceStartupAsync(SetProxyServiceStartupOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> StartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> StopServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> RestartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> WriteProtectedConfigurationAsync(WriteProxyConfigurationOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> RestoreNetworkConfigurationAsync(RestoreProxyNetworkOperation request, CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> RepairServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken);
}

public sealed record ProxyPrivilegedResult(bool Succeeded, string ProblemCode = "");
public sealed record InstallProxyRuntimeOperation(string EngineId, string Version, string ReleaseDirectoryId);
public sealed record RemoveProxyRuntimeOperation(string EngineId);
public sealed record ReplaceProxyRuntimeOperation(string EngineId, string Version, string ReleaseDirectoryId);
public sealed record InstallProxyServiceOperation(string EngineId, string ServiceName, string ConfigurationId);
public sealed record RemoveProxyServiceOperation(string EngineId, string ServiceName);
public sealed record SetProxyServiceStartupOperation(string EngineId, string ServiceName, bool Enabled);
public sealed record ProxyServiceOperation(string EngineId, string ServiceName);
public sealed record WriteProxyConfigurationOperation(string EngineId, string ConfigurationId);
public sealed record RestoreProxyNetworkOperation(string RecoveryMarkerId);

/// <summary>Default until the administrator installs the platform-specific constrained helper.</summary>
public sealed class UnavailableProxyPrivilegedOperations : IProxyPrivilegedOperations
{
    private static readonly ProxyPrivilegedResult Unavailable = new(false, ProxyProblemCodes.PrivilegedOperationUnavailable);
    public Task<ProxyPrivilegedResult> InstallRuntimeAsync(InstallProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> RemoveRuntimeAsync(RemoveProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> ReplaceRuntimeAsync(ReplaceProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> InstallServiceAsync(InstallProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> RemoveServiceAsync(RemoveProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> SetServiceStartupAsync(SetProxyServiceStartupOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> StartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> StopServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> RestartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> WriteProtectedConfigurationAsync(WriteProxyConfigurationOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> RestoreNetworkConfigurationAsync(RestoreProxyNetworkOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<ProxyPrivilegedResult> RepairServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
}
