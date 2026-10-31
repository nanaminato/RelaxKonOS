using System.Diagnostics;
using RelaxKonOS.Server.Privileged;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Server-owned launcher and line-protocol client for the isolated v2 ModuleHost skeleton.</summary>
internal sealed class ModuleHostProcessClient(string packageRoot, string moduleId, string dataPath,
    string? executablePath = null, Action<string>? logError = null, bool isolated = false, string? brokerUrl = null, PrivilegedHelperOptions? helperOptions = null) : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private Process? _process;
    private WindowsModuleHostClient? _windows;
    private Task? _stderr;
    private ModuleHostReady? _ready;
    private readonly string _serviceToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    internal ModuleHostReady Ready => _ready ?? throw new ModulePackageException("module-host-not-started");
    internal string ServiceToken => _serviceToken;

    internal async Task<ModuleHostReady> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null || _windows is not null) return Ready;
        if (isolated && OperatingSystem.IsWindows())
        {
            if (helperOptions is null) throw new ModulePackageException("module-host-helper-unconfigured");
            var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(packageRoot, "manifest.json"), cancellationToken);
            var manifest = ModulePackageJson.Read<ModulePackageManifest>(manifestBytes);
            _windows = new WindowsModuleHostClient(helperOptions, logError);
            await _windows.StartAsync(new ModuleHostLaunchRequest(Guid.NewGuid(), moduleId, manifest.Version,
                Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), _serviceToken, brokerUrl), cancellationToken);
        }
        else
        {
        var (file, arguments) = ResolveExecutable(executablePath);
        if (isolated && (!OperatingSystem.IsLinux() || arguments.Count != 0))
            throw new ModulePackageException("module-host-isolation-unsupported");
        var info = new ProcessStartInfo(isolated ? "/usr/bin/sudo" : file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (isolated)
        {
            info.Environment.Clear();
            info.Environment["PATH"] = "/usr/bin:/bin";
            info.ArgumentList.Add("-n");
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(file);
            info.ArgumentList.Add("--isolated");
        }
        else
        {
            // Development-only unsandboxed probe. Never inherit Server credentials/configuration.
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            var path = Environment.GetEnvironmentVariable("PATH");
            info.Environment.Clear();
            if (systemRoot is not null) info.Environment["SystemRoot"] = systemRoot;
            if (path is not null) info.Environment["PATH"] = path;
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            info.ArgumentList.Add("--package-root"); info.ArgumentList.Add(packageRoot);
            info.ArgumentList.Add("--module-id"); info.ArgumentList.Add(moduleId);
            info.ArgumentList.Add("--data-path"); info.ArgumentList.Add(dataPath);
            info.Environment[ModuleHostProtocol.ServiceTokenEnvironment] = _serviceToken;
            if (brokerUrl is not null) info.Environment[ModulePrivilegeProtocol.BrokerEnvironment] = brokerUrl;
        }
        try { _process = Process.Start(info) ?? throw new ModulePackageException("module-host-start-failed"); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ModulePackageException("module-host-start-failed");
        }
        _stderr = DrainErrorAsync(_process.StandardError);
        if (isolated)
        {
            var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(packageRoot, "manifest.json"), cancellationToken);
            var manifest = ModulePackageJson.Read<ModulePackageManifest>(manifestBytes);
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                moduleId, manifest.Version,
                manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                serviceToken = _serviceToken, brokerUrl,
            }, ModulePackageJson.Options));
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        }
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
        if ((_process is null && _windows is null) || _ready is null) throw new ModulePackageException("module-host-not-started");
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
        if (_windows is not null)
        {
            await _windows.ShutdownAsync(cancellationToken);
            return;
        }
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

    internal async Task AbortAsync()
    {
        if (_windows is not null)
        {
            await _windows.DisposeAsync();
            _windows = null;
            _ready = null;
            return;
        }
        if (_process is null) return;
        _process.StandardInput.Close();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { if (!_process.HasExited) _process.Kill(true); }
        _process.Dispose(); _process = null; _ready = null;
    }

    // Drain in bounded chunks: a module can emit unlimited stderr without a newline.
    private async Task DrainErrorAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var message = new string(buffer, 0, count).Replace(_serviceToken, "[redacted]", StringComparison.Ordinal);
            logError?.Invoke(message);
        }
    }

    private async Task SendAsync(ModuleHostCommand command, CancellationToken cancellationToken)
    {
        if (_windows is not null)
        {
            await _windows.SendAsync(JsonSerializer.Serialize(command, ModuleHostProtocol.Json), cancellationToken);
            return;
        }
        await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, ModuleHostProtocol.Json));
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    private async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        try
        {
            var line = _windows is not null ? await _windows.ReadLineAsync(source.Token)
                : await _process!.StandardOutput.ReadLineAsync(source.Token);
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
        var packaged = Path.Combine(AppContext.BaseDirectory, "module-host", name);
        if (File.Exists(packaged)) return (packaged, []);
        var dll = Path.Combine(AppContext.BaseDirectory, "RelaxKonOS.ServerModuleHost.dll");
        if (File.Exists(dll)) return ("dotnet", [dll]);
        throw new ModulePackageException("module-host-executable-missing");
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        if (_stderr is not null)
            try { await _stderr; } catch (Exception) { }
        if (_windows is not null) await _windows.DisposeAsync();
        _windows = null;
        _process?.Dispose();
        _process = null;
        _ready = null;
    }
}
