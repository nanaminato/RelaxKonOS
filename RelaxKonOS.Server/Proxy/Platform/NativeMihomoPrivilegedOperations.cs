using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy.Platform;

/// <summary>
/// The deployable constrained host boundary for Mihomo. It has no generic command or file API:
/// every executable, service name, argument and target path is derived from fixed constants.
/// Calling it without the required OS rights safely returns a stable unavailable result.
/// </summary>
public sealed class NativeMihomoPrivilegedOperations(
    IProxyPlatformPaths paths,
    RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport transport,
    IWindowsMihomoProcessHost? windowsProcessHost = null,
    IProxyDiagnosticLogStore? diagnostics = null) : IProxyPrivilegedOperations
{
    private const string Engine = Mihomo.MihomoEngine.Id;
    private const string Service = "relaxkonos-mihomo";
    private const string ConfigId = "mihomo-default";
    private const string ActiveLink = "current";

    // Retains the focused runtime-path unit test constructor. Production DI always supplies the
    // platform transport; service actions fail closed if this compatibility constructor is used.
    public NativeMihomoPrivilegedOperations(IProxyPlatformPaths paths)
        : this(paths, new UnavailablePrivilegedTransport(), null, null) { }

    public async Task<ProxyPrivilegedResult> InstallRuntimeAsync(InstallProxyRuntimeOperation request, CancellationToken cancellationToken) =>
        await ActivateRuntimeAsync(request.EngineId, request.Version, request.ReleaseDirectoryId, cancellationToken);
    public async Task<ProxyPrivilegedResult> ReplaceRuntimeAsync(ReplaceProxyRuntimeOperation request, CancellationToken cancellationToken)
    {
        var activated = await ActivateRuntimeAsync(request.EngineId, request.Version, request.ReleaseDirectoryId, cancellationToken);
        return activated;
    }
    public async Task<ProxyPrivilegedResult> RemoveRuntimeAsync(RemoveProxyRuntimeOperation request, CancellationToken cancellationToken)
    {
        if (request.EngineId != Engine) return Invalid();
        try
        {
            var active = ActivePath();
            if (Directory.Exists(active) || File.Exists(active)) Directory.Delete(active, recursive: true);
            var versions = paths.GetEngineVersionsDirectory(Engine);
            if (Directory.Exists(versions)) Directory.Delete(versions, recursive: true);
            return Success();
        }
        catch (IOException)
        {
            await WriteDiagnosticAsync("error", "Managed Mihomo runtime cleanup could not delete the runtime directory; a service process may still be using it.", cancellationToken);
            return Unavailable();
        }
        catch (UnauthorizedAccessException)
        {
            await WriteDiagnosticAsync("error", "Managed Mihomo runtime cleanup was denied access to the runtime directory.", cancellationToken);
            return Unavailable();
        }
    }

    public async Task<ProxyPrivilegedResult> InstallServiceAsync(InstallProxyServiceOperation request, CancellationToken cancellationToken)
    {
        if (!IsServiceRequest(request.EngineId, request.ServiceName) || request.ConfigurationId != ConfigId) return Invalid();
        if (OperatingSystem.IsLinux())
        {
            var installed = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ProxyMihomoInstallSystemService), cancellationToken);
            if (!installed.Success) return Unavailable();
            var reload = await SystemctlAsync(["daemon-reload"], cancellationToken);
            return reload.Succeeded ? await SystemctlAsync(["enable", Service], cancellationToken) : reload;
        }
        if (OperatingSystem.IsWindows())
        {
            // Windows deliberately has no second SCM service: RelaxKonOS.Server owns the child
            // process and its IHostedService shutdown path. The first start happens below.
            return windowsProcessHost is null ? Unavailable() : Success();
        }
        return Unsupported();
    }

    public async Task<ProxyPrivilegedResult> RemoveServiceAsync(RemoveProxyServiceOperation request, CancellationToken cancellationToken)
    {
        if (!IsServiceRequest(request.EngineId, request.ServiceName)) return Invalid();
        if (OperatingSystem.IsLinux())
        {
            var disable = await SystemctlAsync(["disable", Service], cancellationToken);
            var removed = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ProxyMihomoRemoveSystemService), cancellationToken);
            if (!removed.Success) return Unavailable();
            var reload = await SystemctlAsync(["daemon-reload"], cancellationToken);
            return disable.Succeeded || reload.Succeeded ? Success() : disable;
        }
        if (OperatingSystem.IsWindows()) return windowsProcessHost is null ? Unavailable() : await windowsProcessHost.StopAsync(cancellationToken);
        return Unsupported();
    }

    public Task<ProxyPrivilegedResult> SetServiceStartupAsync(SetProxyServiceStartupOperation request, CancellationToken cancellationToken)
    {
        if (!IsServiceRequest(request.EngineId, request.ServiceName)) return Task.FromResult(Invalid());
        if (OperatingSystem.IsLinux()) return SystemctlAsync([request.Enabled ? "enable" : "disable", Service], cancellationToken);
        if (OperatingSystem.IsWindows()) return Task.FromResult(windowsProcessHost is null ? Unavailable() : Success());
        return Task.FromResult(Unsupported());
    }
    public Task<ProxyPrivilegedResult> StartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => ServiceActionAsync(request, "start", cancellationToken);
    public Task<ProxyPrivilegedResult> StopServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => ServiceActionAsync(request, "stop", cancellationToken);
    public Task<ProxyPrivilegedResult> RestartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => ServiceActionAsync(request, "restart", cancellationToken);
    public Task<ProxyPrivilegedResult> RepairServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => ServiceActionAsync(request, "try-restart", cancellationToken);

    public Task<ProxyPrivilegedResult> WriteProtectedConfigurationAsync(WriteProxyConfigurationOperation request, CancellationToken cancellationToken) =>
        Task.FromResult(request.EngineId == Engine && request.ConfigurationId == ConfigId ? Unavailable() : Invalid());
    public Task<ProxyPrivilegedResult> RestoreNetworkConfigurationAsync(RestoreProxyNetworkOperation request, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(request.RecoveryMarkerId) ? Invalid() : Unavailable());

    private async Task<ProxyPrivilegedResult> ActivateRuntimeAsync(string engineId, string version, string releaseDirectoryId, CancellationToken cancellationToken)
    {
        if (engineId != Engine || !IsRelease(version, releaseDirectoryId)) return Invalid();
        var release = Path.Combine(paths.GetEngineVersionsDirectory(Engine), releaseDirectoryId);
        if (!File.Exists(Path.Combine(release, BinaryName()))) return new(false, ProxyProblemCodes.RuntimeNotInstalled);
        try
        {
            var active = ActivePath();
            if (OperatingSystem.IsLinux())
            {
                var temporary = active + ".new";
                try
                {
                    // A stale temporary link may be left behind by an interrupted activation.
                    // unlink(2) removes the link itself and never follows its directory target.
                    DeleteLinuxLinkIfPresent(temporary);
                    Directory.CreateSymbolicLink(temporary, release);

                    // Use rename(2) directly instead of File.Move. The source is a symbolic
                    // link to a directory, which File.Move has reported as unavailable on some
                    // Linux hosts. rename atomically replaces an existing current link without
                    // stopping a running service in an intermediate no-runtime state.
                    RenameLinuxLink(temporary, active);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    DeleteLinuxLinkIfPresentIgnoringFailure(temporary);
                    await WriteDiagnosticAsync("warning", "Managed Mihomo runtime link activation failed: " + DescribeLinkActivationFailure(exception), cancellationToken);
                    return Unavailable();
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(active + ".txt", releaseDirectoryId, cancellationToken);
            }
            else return Unsupported();
            return Success();
        }
        catch (IOException) { return Unavailable(); }
        catch (UnauthorizedAccessException) { return Unavailable(); }
    }

    private Task<ProxyPrivilegedResult> ServiceActionAsync(ProxyServiceOperation request, string action, CancellationToken cancellationToken)
    {
        if (!IsServiceRequest(request.EngineId, request.ServiceName)) return Task.FromResult(Invalid());
        if (OperatingSystem.IsLinux()) return SystemctlAsync([action, Service], cancellationToken);
        if (OperatingSystem.IsWindows()) return WindowsProcessActionAsync(action, cancellationToken);
        return Task.FromResult(Unsupported());
    }
    private static bool IsServiceRequest(string engineId, string serviceName) => engineId == Engine && serviceName == Service;
    private static bool IsRelease(string version, string releaseId) => version == Mihomo.MihomoRuntimeManifest.SupportedVersion && releaseId == version + "-" + Mihomo.MihomoRuntimeManifest.CurrentRid();

    private static void RenameLinuxLink(string source, string destination)
    {
        if (Rename(source, destination) == 0) return;
        throw new LinuxLinkOperationException("rename", Marshal.GetLastPInvokeError());
    }

    private static void DeleteLinuxLinkIfPresent(string path)
    {
        if (Unlink(path) == 0) return;
        var error = Marshal.GetLastPInvokeError();
        if (error == 2) return; // ENOENT
        throw new LinuxLinkOperationException("unlink", error);
    }

    private static void DeleteLinuxLinkIfPresentIgnoringFailure(string path)
    {
        try { DeleteLinuxLinkIfPresent(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string DescribeLinkActivationFailure(Exception exception) => exception is LinuxLinkOperationException linux
        ? linux.Operation + " failed with errno=" + linux.ErrorNumber
        : exception.GetType().Name;

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string source, string destination);

    [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static extern int Unlink(string path);

    private sealed class LinuxLinkOperationException(string operation, int errorNumber) : IOException
    {
        public string Operation { get; } = operation;
        public int ErrorNumber { get; } = errorNumber;
    }

    private string ActivePath() => Path.Combine(paths.GetEngineVersionsDirectory(Engine), ActiveLink);
    private Task<ProxyPrivilegedResult> WindowsProcessActionAsync(string action, CancellationToken cancellationToken)
    {
        if (windowsProcessHost is null) return Task.FromResult(Unavailable());
        return action switch
        {
            "start" => windowsProcessHost.StartAsync(cancellationToken),
            "stop" => windowsProcessHost.StopAsync(cancellationToken),
            "restart" or "try-restart" => windowsProcessHost.RestartAsync(cancellationToken),
            _ => Task.FromResult(Invalid()),
        };
    }
    private static string BinaryName() => OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo";
    private async Task<ProxyPrivilegedResult> SystemctlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var action = arguments.ToArray() switch
        {
            ["daemon-reload"] => ProxyMihomoServiceAction.DaemonReload,
            ["enable", Service] => ProxyMihomoServiceAction.Enable,
            ["disable", Service] => ProxyMihomoServiceAction.Disable,
            ["start", Service] => ProxyMihomoServiceAction.Start,
            ["stop", Service] => ProxyMihomoServiceAction.Stop,
            ["restart", Service] => ProxyMihomoServiceAction.Restart,
            ["try-restart", Service] => ProxyMihomoServiceAction.TryRestart,
            _ => throw new InvalidOperationException("Unsupported fixed Mihomo systemd action."),
        };
        var result = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ProxyMihomoServiceAction,
            ProxyMihomoServiceAction: action), cancellationToken);
        return result.Success ? Success() : Unavailable();
    }
    private async Task WriteDiagnosticAsync(string level, string message, CancellationToken cancellationToken)
    {
        if (diagnostics is not null) await diagnostics.WriteAsync(level, message, cancellationToken);
    }
    private static ProxyPrivilegedResult Success() => new(true);
    private static ProxyPrivilegedResult Invalid() => new(false, ProxyProblemCodes.NotSupported);
    private static ProxyPrivilegedResult Unavailable() => new(false, ProxyProblemCodes.PrivilegedOperationUnavailable);
    private static ProxyPrivilegedResult Unsupported() => new(false, ProxyProblemCodes.RuntimeUnsupportedPlatform);

    private sealed class UnavailablePrivilegedTransport : RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrivilegedOperationResult(false, 69, Error: "privileged transport unavailable", ProblemCode: PrivilegedProblemCode.HelperUnavailable));
    }
}
