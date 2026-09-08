using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>
/// Windows owns Mihomo as a child of RelaxKonOS.Server.  This is deliberately a singleton so
/// start, stop, update and host shutdown all operate on the same process tree.
/// </summary>
public interface IWindowsMihomoProcessHost
{
    Task<ProxyPrivilegedResult> StartAsync(CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> StopAsync(CancellationToken cancellationToken);
    Task<ProxyPrivilegedResult> RestartAsync(CancellationToken cancellationToken);
}

public sealed class WindowsMihomoProcessHost(
    IProxyPlatformPaths paths,
    IProxyDiagnosticLogStore? diagnostics = null) : IWindowsMihomoProcessHost, IHostedService, IDisposable
{
    private const string Engine = MihomoEngine.Id;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _hostStopping = new();
    private Process? _process;
    private bool _shouldRun;
    private bool _disposed;

    // Installing the Server process starts no proxy implicitly. The runtime manager decides when
    // a verified runtime should run; this hosted-service hook exists to guarantee shutdown cleanup.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _hostStopping.Cancel();
        await StopManagedAsync(cancellationToken);
    }

    Task<ProxyPrivilegedResult> IWindowsMihomoProcessHost.StartAsync(CancellationToken cancellationToken) => StartManagedAsync(cancellationToken, setDesiredState: true);
    Task<ProxyPrivilegedResult> IWindowsMihomoProcessHost.StopAsync(CancellationToken cancellationToken) => StopManagedAsync(cancellationToken);

    async Task<ProxyPrivilegedResult> IWindowsMihomoProcessHost.RestartAsync(CancellationToken cancellationToken)
    {
        var stopped = await StopManagedAsync(cancellationToken);
        return !stopped.Succeeded ? stopped : await StartManagedAsync(cancellationToken, setDesiredState: true);
    }

    private async Task<ProxyPrivilegedResult> StartManagedAsync(CancellationToken cancellationToken, bool setDesiredState)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (setDesiredState) _shouldRun = true;
            if (!_shouldRun) return Success();
            if (_process is { HasExited: false }) return Success();
            DisposeExitedProcess();

            var executable = ActiveBinaryPath();
            var configuration = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
            if (!File.Exists(executable) || !File.Exists(configuration))
            {
                await WriteDiagnosticAsync("warning", "Managed Mihomo could not start because its active runtime or protected configuration is missing.");
                return Unavailable();
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            var dataDirectory = paths.GetEngineDataDirectory(Engine);
            Directory.CreateDirectory(dataDirectory);
            process.StartInfo.ArgumentList.Add("-d");
            process.StartInfo.ArgumentList.Add(dataDirectory);
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(configuration);

            if (!process.Start())
            {
                process.Dispose();
                return Unavailable();
            }
            // Read each pipe to EOF instead of relying on OutputDataReceived. A process that
            // terminates during startup can otherwise lose its final parse error when the Exited
            // callback disposes the Process before the event queue has drained.
            var standardOutput = CaptureProcessOutputAsync(process.StandardOutput, "info");
            var standardError = CaptureProcessOutputAsync(process.StandardError, "warning");
            // Assign before enabling exit notifications. Enabling events on an already-exited
            // process may invoke the callback immediately.
            _process = process;
            process.Exited += (_, _) => _ = HandleExitedAsync(process, standardOutput, standardError);
            process.EnableRaisingEvents = true;
            await WriteDiagnosticAsync("info", "Managed Mihomo started as a child process of RelaxKonOS.Server. pid=" + process.Id + ".");
            return Success();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
        {
            await WriteDiagnosticAsync("error", "Managed Mihomo could not be started: " + exception.GetType().Name);
            return Unavailable();
        }
        finally { _gate.Release(); }
    }

    private async Task<ProxyPrivilegedResult> StopManagedAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        Process? process;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _shouldRun = false;
            process = _process;
            _process = null;
        }
        finally { _gate.Release(); }

        if (process is null) return Success();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            await WriteDiagnosticAsync("info", "Managed Mihomo process tree stopped with RelaxKonOS.Server supervision.");
            return Success();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        {
            await WriteDiagnosticAsync("error", "Managed Mihomo process tree could not be stopped: " + exception.GetType().Name);
            return Unavailable();
        }
        finally { process.Dispose(); }
    }

    private async Task HandleExitedAsync(Process process, Task standardOutput, Task standardError)
    {
        var exitCode = TryGetExitCode(process);
        var restart = false;
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
            restart = _shouldRun && !_hostStopping.IsCancellationRequested;
        }
        finally { _gate.Release(); }

        try { await Task.WhenAll(standardOutput, standardError); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        process.Dispose();
        await WriteDiagnosticAsync("warning", "Managed Mihomo exited unexpectedly" + (exitCode is { } code ? " with exit code " + code : "") + ".");
        if (!restart) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _hostStopping.Token);
            await StartManagedAsync(_hostStopping.Token, setDesiredState: false);
        }
        catch (OperationCanceledException) { }
    }

    private void DisposeExitedProcess()
    {
        if (_process is not { HasExited: true } exited) return;
        _process = null;
        exited.Dispose();
    }

    private string ActiveBinaryPath()
    {
        var versions = paths.GetEngineVersionsDirectory(Engine);
        try
        {
            var releaseId = File.ReadAllText(Path.Combine(versions, "current.txt")).Trim();
            if (releaseId == MihomoRuntimeManifest.SupportedVersion + "-" + MihomoRuntimeManifest.CurrentRid())
                return Path.Combine(versions, releaseId, "mihomo.exe");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Path.Combine(versions, "current", "mihomo.exe");
    }

    private async Task WriteDiagnosticAsync(string level, string message)
    {
        if (diagnostics is not null) await diagnostics.WriteAsync(level, message, CancellationToken.None);
    }

    private async Task CaptureProcessOutputAsync(StreamReader reader, string level)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } output)
                if (!string.IsNullOrWhiteSpace(output)) await WriteDiagnosticAsync(level, "mihomo: " + output);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsMihomoProcessHost));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hostStopping.Cancel();
        // Exited callbacks can still be queued while the host disposes services. Leave the
        // synchronization primitives alive until process teardown completes with the RelaxKonOS.Server.
    }
    private static ProxyPrivilegedResult Success() => new(true);
    private static ProxyPrivilegedResult Unavailable() => new(false, ProxyProblemCodes.PrivilegedOperationUnavailable);
    private static ProxyPrivilegedResult Unsupported() => new(false, ProxyProblemCodes.RuntimeUnsupportedPlatform);
}
