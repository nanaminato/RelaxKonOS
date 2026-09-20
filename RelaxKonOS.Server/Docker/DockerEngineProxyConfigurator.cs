using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Docker;

/// <summary>Outcome of installing or removing the daemon-layer proxy on this host.</summary>
/// <param name="Detail">A localization key for an extra explanation, or empty.</param>
public sealed record DockerEngineProxyApplyResult(bool Success, string ProblemCode, string Detail = "");

/// <summary>
/// Owns the daemon layer. This is the only place that knows a platform mechanism exists, so
/// endpoints, the service, and the client never branch on the operating system.
/// </summary>
public interface IDockerEngineProxyConfigurator
{
    /// <summary>Stable platform identifier shown to the operator, e.g. <c>linux-systemd</c>.</summary>
    string Platform { get; }
    /// <summary>Whether this host can install a daemon proxy at all.</summary>
    bool IsSupported { get; }
    /// <summary>Installs (or removes) the daemon proxy. The caller owns confirmation.</summary>
    Task<DockerEngineProxyApplyResult> ApplyAsync(bool enabled, DockerProxyResolution resolution, CancellationToken cancellationToken = default);
}

public sealed class DockerEngineProxyConfigurator(IPrivilegedOperationTransport transport, ILogger<DockerEngineProxyConfigurator> logger)
    : IDockerEngineProxyConfigurator
{
    public string Platform => OperatingSystem.IsLinux() ? "linux-systemd" : OperatingSystem.IsWindows() ? "docker-desktop-windows" : "unsupported";

    public bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    public async Task<DockerEngineProxyApplyResult> ApplyAsync(bool enabled, DockerProxyResolution resolution, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux()) return await ApplyLinuxAsync(enabled, resolution, cancellationToken);
        if (OperatingSystem.IsWindows()) return await ApplyDockerDesktopAsync(enabled, resolution, cancellationToken);
        return new(false, DockerProxyProblem.PlatformUnsupported);
    }

    /// <summary>
    /// A native Linux daemon reads its proxy from the service's start-up environment, which only the
    /// privileged Helper can write. The Helper also reloads and restarts the unit, so the returned
    /// state is "written" and the daemon's own read-back decides whether it is live.
    /// </summary>
    private async Task<DockerEngineProxyApplyResult> ApplyLinuxAsync(bool enabled, DockerProxyResolution resolution, CancellationToken cancellationToken)
    {
        var configuration = enabled
            ? new DockerProxyConfiguration(resolution.HttpProxy, resolution.HttpsProxy, resolution.NoProxy, Enabled: true)
            : new DockerProxyConfiguration(string.Empty, string.Empty, string.Empty, Enabled: false);
        var result = await transport.ExecuteAsync(
            new PrivilegedOperationRequest(PrivilegedOperationKind.DockerEngineConfigureProxy, DockerProxy: configuration),
            cancellationToken);
        if (result.Success) return new(true, string.Empty, DockerProxyDetail.RestartPending);
        logger.LogWarning("Docker daemon proxy configuration failed. ProblemCode={ProblemCode}", result.ProblemCode);
        return new(false, result.ProblemCode switch
        {
            PrivilegedProblemCode.UnsupportedOperation => DockerProxyProblem.PlatformUnsupported,
            PrivilegedProblemCode.HelperUnavailable => DockerProxyProblem.HelperUnavailable,
            PrivilegedProblemCode.AccessDenied => DockerProxyProblem.HelperUnavailable,
            _ => DockerProxyProblem.EngineApplyFailed,
        });
    }

    /// <summary>
    /// Docker Desktop owns the daemon inside its own VM and ignores <c>daemon.json</c>, so the only
    /// supported route is its per-user settings file. Docker Desktop rewrites that file while
    /// shutting down, so it is stopped first and started again afterwards; when the Server cannot
    /// drive the desktop application, the write still lands and the operator restarts it.
    /// </summary>
    private async Task<DockerEngineProxyApplyResult> ApplyDockerDesktopAsync(bool enabled, DockerProxyResolution resolution, CancellationToken cancellationToken)
    {
        var path = DockerDesktopProxySettings.ResolveSettingsPath();
        if (path is null) return new(false, DockerProxyProblem.DesktopSettingsNotFound);

        if (!await DockerDesktopProxySettings.RunDesktopCommandAsync("stop", TimeSpan.FromMinutes(2), cancellationToken))
            return new(false, DockerProxyProblem.DesktopStopFailed);

        var started = false;
        try
        {
            if (!DockerDesktopProxySettings.TryWrite(path, resolution.HttpProxy, resolution.HttpsProxy, resolution.NoProxy, enabled, out var problemCode))
                return new(false, problemCode);

            // Read the file back rather than trusting the write: Docker Desktop can present a locked or
            // replaced file, and a proxy that silently failed to stick would otherwise be reported as
            // installed until the operator noticed the daemon was still using the old upstream.
            if (DockerDesktopProxySettings.TryRead(path) is not { } stored) return new(false, DockerProxyProblem.DesktopSettingsUnreadable);
            var http = string.Equals(stored.Mode, DockerDesktopProxySettings.ManualMode, StringComparison.OrdinalIgnoreCase)
                ? stored.HttpProxy.Trim()
                : string.Empty;
            if (enabled && !string.Equals(http, resolution.HttpProxy, StringComparison.OrdinalIgnoreCase))
                return new(false, DockerProxyProblem.DesktopSettingsNotApplied);

            started = await DockerDesktopProxySettings.RunDesktopCommandAsync("start", TimeSpan.FromMinutes(3), cancellationToken);
            return new(true, string.Empty, started ? DockerProxyDetail.RestartPending : DockerProxyDetail.DesktopRestartPending);
        }
        finally
        {
            // Once Docker Desktop was stopped, a failed write, read-back, or cancelled request must
            // not leave every workload offline. Recovery intentionally ignores the caller token.
            if (!started)
            {
                try { await DockerDesktopProxySettings.RunDesktopCommandAsync("start", TimeSpan.FromMinutes(3), CancellationToken.None); }
                catch { /* Preserve the configuration failure; the caller receives its stable code. */ }
            }
        }
    }
}
