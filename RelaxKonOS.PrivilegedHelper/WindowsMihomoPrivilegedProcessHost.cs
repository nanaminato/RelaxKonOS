using System.Diagnostics;
using System.Runtime.Versioning;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Owns the one fixed managed Mihomo process for a Windows Helper service.  The Server runs as
/// LocalService, which deliberately cannot create a Wintun adapter; this LocalSystem boundary
/// supplies that capability without accepting an executable path, arguments, or environment.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMihomoPrivilegedProcessHost
{
    private const string EngineId = "mihomo";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;
    private static bool _shouldRun;

    public static Task<PrivilegedOperationResult> InstallAsync() => Task.FromResult(Success());

    public static async Task<PrivilegedOperationResult> RemoveAsync()
    {
        var stopped = await StopAsync(CancellationToken.None);
        return stopped.Success ? Success() : stopped;
    }

    public static async Task<PrivilegedOperationResult> ApplyAsync(ProxyMihomoServiceAction? action)
    {
        if (action is null) return Invalid();
        return action.Value switch
        {
            ProxyMihomoServiceAction.Start => await StartAsync(CancellationToken.None, setDesiredState: true),
            ProxyMihomoServiceAction.Stop or ProxyMihomoServiceAction.Disable => await StopAsync(CancellationToken.None),
            ProxyMihomoServiceAction.Restart or ProxyMihomoServiceAction.TryRestart => await RestartAsync(CancellationToken.None),
            // The Helper has no SCM unit to reload or enable. These lifecycle declarations are
            // nevertheless acknowledged so the Server can use one closed cross-platform API.
            ProxyMihomoServiceAction.DaemonReload or ProxyMihomoServiceAction.Enable => Success(),
            _ => Invalid(),
        };
    }

    public static Task StopForHelperShutdownAsync() => StopAsync(CancellationToken.None);

    private static async Task<PrivilegedOperationResult> StartAsync(CancellationToken cancellationToken, bool setDesiredState = true)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (setDesiredState) _shouldRun = true;
            if (!_shouldRun) return Success();
            if (_process is { HasExited: false }) return Success();
            DisposeExitedProcess();

            var executable = ActiveBinaryPath();
            var configuration = Path.Combine(ProxyRoot(), "config", "active.yaml");
            if (executable is null || !File.Exists(configuration)) return Unavailable();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-d");
            process.StartInfo.ArgumentList.Add(Path.Combine(ProxyRoot(), "engines", EngineId, "data"));
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(configuration);
            if (!process.Start())
            {
                process.Dispose();
                return Unavailable();
            }
            _process = process;
            process.Exited += (_, _) => _ = RestartAfterUnexpectedExitAsync(process);
            process.EnableRaisingEvents = true;
            return Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Unavailable();
        }
        finally { Gate.Release(); }
    }

    private static async Task<PrivilegedOperationResult> RestartAsync(CancellationToken cancellationToken)
    {
        var stopped = await StopAsync(cancellationToken);
        return stopped.Success ? await StartAsync(cancellationToken, setDesiredState: true) : stopped;
    }

    private static async Task<PrivilegedOperationResult> StopAsync(CancellationToken cancellationToken)
    {
        Process? process;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            _shouldRun = false;
            process = _process;
            _process = null;
        }
        finally { Gate.Release(); }

        if (process is null) return Success();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            return Success();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        {
            return Unavailable();
        }
        finally { process.Dispose(); }
    }

    private static void DisposeExitedProcess()
    {
        if (_process is not { HasExited: true } exited) return;
        _process = null;
        exited.Dispose();
    }

    private static async Task RestartAfterUnexpectedExitAsync(Process process)
    {
        var restart = false;
        await Gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
            restart = _shouldRun;
        }
        finally { Gate.Release(); }

        process.Dispose();
        if (!restart) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await StartAsync(CancellationToken.None, setDesiredState: false);
        }
        catch (OperationCanceledException) { }
    }

    private static string? ActiveBinaryPath()
    {
        var versions = Path.Combine(ProxyRoot(), "engines", EngineId, "versions");
        try
        {
            var release = File.ReadAllText(Path.Combine(versions, "current.txt")).Trim();
            // The Server only writes an immutable manifest release ID. Reject all path syntax
            // before constructing the fixed binary path, even though the Server controls it.
            if (!System.Text.RegularExpressions.Regex.IsMatch(release, "^v[0-9]+(?:\\.[0-9]+){2}-win-(?:x64|arm64)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return null;
            var candidate = Path.GetFullPath(Path.Combine(versions, release, "mihomo.exe"));
            var root = Path.GetFullPath(versions + Path.DirectorySeparatorChar);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string ProxyRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "Proxy");
    private static PrivilegedOperationResult Success() => new(true);
    private static PrivilegedOperationResult Invalid() => new(false, 64, Error: "invalid proxy service action", ProblemCode: PrivilegedProblemCode.InvalidRequest);
    private static PrivilegedOperationResult Unavailable() => new(false, 69, Error: "managed mihomo process is unavailable", ProblemCode: PrivilegedProblemCode.HelperUnavailable);
}
