using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Server-owned launcher and line-protocol client for the isolated v2 ModuleHost skeleton.</summary>
internal sealed class ModuleHostProcessClient(string packageRoot, string moduleId, string dataPath,
    string? executablePath = null) : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private Process? _process;
    private Task<string>? _stderr;
    private ModuleHostReady? _ready;
    private readonly string _serviceToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    internal ModuleHostReady Ready => _ready ?? throw new ModulePackageException("module-host-not-started");
    internal string ServiceToken => _serviceToken;

    internal async Task<ModuleHostReady> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null) return Ready;
        var (file, arguments) = ResolveExecutable(executablePath);
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--package-root"); info.ArgumentList.Add(packageRoot);
        info.ArgumentList.Add("--module-id"); info.ArgumentList.Add(moduleId);
        info.ArgumentList.Add("--data-path"); info.ArgumentList.Add(dataPath);
        info.Environment[ModuleHostProtocol.ServiceTokenEnvironment] = _serviceToken;
        try { _process = Process.Start(info) ?? throw new ModulePackageException("module-host-start-failed"); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ModulePackageException("module-host-start-failed");
        }
        _stderr = _process.StandardError.ReadToEndAsync(cancellationToken);
        var line = await ReadLineAsync(StartupTimeout, cancellationToken);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.GetProperty("type").GetString() != "ready")
            throw new ModulePackageException(root.TryGetProperty("problemCode", out var code)
                ? code.GetString() ?? "module-host-start-failed" : "module-host-start-failed");
        _ready = new ModuleHostReady("ready", root.GetProperty("moduleId").GetString() ?? "",
            root.GetProperty("version").GetString() ?? "", root.GetProperty("hostApiVersion").GetString() ?? "",
            root.GetProperty("url").GetString() ?? "");
        if (_ready.ModuleId != moduleId) throw new ModulePackageException("module-host-entry-id-mismatch");
        if (!Uri.TryCreate(_ready.Url, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "http" || !endpoint.IsLoopback)
            throw new ModulePackageException("module-host-response-invalid");
        return _ready;
    }

    internal async Task<ModuleHostHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null || _ready is null) throw new ModulePackageException("module-host-not-started");
        await SendAsync(new ModuleHostCommand(ModuleHostProtocol.Health), cancellationToken);
        var line = await ReadLineAsync(CommandTimeout, cancellationToken);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.GetProperty("type").GetString() != "health")
            throw new ModulePackageException("module-host-response-invalid");
        return new ModuleHostHealth("health", root.GetProperty("healthy").GetBoolean(),
            root.TryGetProperty("problemCode", out var code) ? code.GetString() : null);
    }

    internal async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null) return;
        try
        {
            await SendAsync(new ModuleHostCommand(ModuleHostProtocol.Shutdown), cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { }
        finally
        {
            if (_process is { HasExited: false }) { try { _process.Kill(true); } catch (InvalidOperationException) { } }
        }
    }

    private async Task SendAsync(ModuleHostCommand command, CancellationToken cancellationToken)
    {
        await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, ModuleHostProtocol.Json));
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    private async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        try
        {
            var line = await _process!.StandardOutput.ReadLineAsync(source.Token);
            if (string.IsNullOrWhiteSpace(line) || line.Length > 16384)
                throw new ModulePackageException("module-host-response-invalid");
            return line;
        }
        catch (OperationCanceledException) { throw new ModulePackageException("module-host-timeout"); }
    }

    private static (string File, IReadOnlyList<string> Arguments) ResolveExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new ModulePackageException("module-host-executable-missing");
            return (configured, []);
        }
        var name = OperatingSystem.IsWindows() ? "RelaxKonOS.ServerModuleHost.exe" : "RelaxKonOS.ServerModuleHost";
        var native = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(native)) return (native, []);
        var dll = Path.Combine(AppContext.BaseDirectory, "RelaxKonOS.ServerModuleHost.dll");
        if (File.Exists(dll)) return ("dotnet", [dll]);
        throw new ModulePackageException("module-host-executable-missing");
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        if (_stderr is not null)
            try { await _stderr; } catch (Exception) { }
        _process?.Dispose();
    }
}
