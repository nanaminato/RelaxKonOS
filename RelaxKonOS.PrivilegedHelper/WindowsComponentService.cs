using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.PrivilegedHelper;

internal enum ComponentKind { Nginx, Mihomo, Frpc, Frps }
internal sealed record ComponentServiceConfiguration(ComponentKind Kind, string Name, string Root,
    string Executable, string Configuration, string? DataDirectory, string BinaryHash, string? AppliedIdentity);
internal sealed record ComponentProcessState(int ProcessId, DateTime StartedAt, bool Connected,
    bool AuthenticationFailed, IReadOnlyList<TunnelLogEntryDto> Logs);

/// <summary>Independent SCM host. It has no Helper pipe or remote command surface.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsComponentService : ServiceBase
{
    private readonly ComponentServiceConfiguration _configuration;
    private readonly string _statePath;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private Process? _child;
    private Microsoft.Win32.SafeHandles.SafeFileHandle? _job;
    private readonly object _stateGate = new();
    private readonly Queue<TunnelLogEntryDto> _logs = new();
    private bool _connected, _authenticationFailed;

    private WindowsComponentService(ComponentServiceConfiguration configuration, string path)
    {
        _configuration = configuration;
        _statePath = Path.ChangeExtension(path, ".state.json");
        ServiceName = configuration.Name;
        CanStop = true;
    }

    public static void Run(string[] args)
    {
        var index = Array.IndexOf(args, "--config");
        if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("Component service configuration is required.");
        var path = Path.GetFullPath(args[index + 1]);
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
        RequireProtected(path);
        var config = JsonSerializer.Deserialize<ComponentServiceConfiguration>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        Validate(config);
        ServiceBase.Run(new WindowsComponentService(config, path));
    }

    internal static void Validate(ComponentServiceConfiguration config)
    {
        if (!Enum.IsDefined(config.Kind) || config.Name != Name(config.Kind, config.Root, config.Kind == ComponentKind.Frpc ? Path.GetFileName(Path.GetDirectoryName(config.Configuration)) : null))
            throw new InvalidDataException("Invalid component service identity.");
        var binary = config.Kind switch { ComponentKind.Nginx => "nginx.exe", ComponentKind.Mihomo => "mihomo.exe", ComponentKind.Frpc => "frpc.exe", _ => "frps.exe" };
        if (!Path.IsPathFullyQualified(config.Root) || !WindowsManagedRuntimePolicy.Contains(config.Root, config.Executable)
            || !WindowsManagedRuntimePolicy.Contains(config.Root, config.Configuration) || Path.GetFileName(config.Executable) != binary
            || config.BinaryHash.Length != 64) throw new InvalidDataException("Invalid component paths.");
        WindowsManagedRuntimePolicy.RequireNoLinks(config.Executable);
        WindowsManagedRuntimePolicy.RequireNoLinks(config.Configuration);
        if (config.DataDirectory is not null && !WindowsManagedRuntimePolicy.Contains(config.Root, config.DataDirectory)) throw new InvalidDataException();
    }

    internal static string Name(ComponentKind kind, string root, string? instance = null)
    {
        if (!Enum.IsDefined(kind) || !Path.IsPathFullyQualified(root)) throw new InvalidDataException("Invalid component scope.");
        if (kind == ComponentKind.Frpc && (!Guid.TryParseExact(instance, "N", out var id) || id == Guid.Empty)) throw new InvalidDataException("FRPC requires a profile identity.");
        var identity = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant())))[..12];
        return $"RelaxKonOSComponent-{kind}-{identity}" + (kind == ComponentKind.Frpc ? "-" + instance : "");
    }

    private static string HostRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS-Components");
    private static string ConfigPath(string name) => Path.Combine(HostRoot, "services", name + ".json");

    internal static async Task InstallAsync(ComponentKind kind, string root, string executable, string configuration,
        string? dataDirectory = null, string? instance = null, string? appliedIdentity = null)
    {
        var name = Name(kind, root, instance);
        if (Exists(name)) EnsureOwnedService(name, kind, root);
        var config = new ComponentServiceConfiguration(kind, name, Path.GetFullPath(root), Path.GetFullPath(executable),
            Path.GetFullPath(configuration), dataDirectory, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable))), appliedIdentity);
        Validate(config);
        var sourceRoot = AppContext.BaseDirectory;
        var sourceExe = Path.Combine(sourceRoot, "RelaxKonOS.PrivilegedHelper.exe");
        RequireProtected(sourceExe);
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        using var packageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in sourceFiles)
        {
            WindowsManagedRuntimePolicy.RequireNoLinks(file); RequireProtected(file);
            packageHash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(sourceRoot, file)));
            packageHash.AppendData(SHA256.HashData(await File.ReadAllBytesAsync(file)));
        }
        var hostVersion = Convert.ToHexString(packageHash.GetHashAndReset())[..24];
        var destination = Path.Combine(HostRoot, "host", hostVersion);
        ProtectDirectory(HostRoot); ProtectDirectory(Path.Combine(HostRoot, "host"));
        if (!Directory.Exists(destination))
        {
            var staging = destination + ".new";
            ProtectDirectory(staging);
            foreach (var file in sourceFiles)
            {
                var target = Path.Combine(staging, Path.GetRelativePath(sourceRoot, file));
                ProtectDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            Directory.Move(staging, destination);
        }
        ProtectDirectory(Path.Combine(HostRoot, "services"));
        var path = ConfigPath(name);
        await File.WriteAllTextAsync(path + ".new", JsonSerializer.Serialize(config));
        File.Move(path + ".new", path, true);
        var host = Path.Combine(destination, "RelaxKonOS.PrivilegedHelper.exe");
        var command = $"\"{host}\" --component-service --config \"{path}\"";
        await ScAsync(Exists(name) ? "config" : "create", name, "binPath=", command, "start=", "auto", "obj=", "LocalSystem");
        await ScAsync("failure", name, "reset=", "86400", "actions=", "restart/3000/restart/10000/restart/30000");
    }

    internal static async Task StartAsync(ComponentKind kind, string root, string? instance = null)
    {
        var name = Name(kind, root, instance);
        EnsureOwnedService(name, kind, root);
        using var service = new ServiceController(name);
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start();
        await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)));
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (Snapshot(kind, root, instance).Running) return;
            await Task.Delay(100);
        }
        throw new IOException("Independent component process did not become ready.");
    }
    internal static async Task StopAsync(ComponentKind kind, string root, string? instance = null)
    {
        var name = Name(kind, root, instance);
        if (!Exists(name)) return;
        EnsureOwnedService(name, kind, root);
        using var service = new ServiceController(name);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        service.Stop();
        await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)));
    }
    internal static async Task RemoveAsync(ComponentKind kind, string root, string? instance = null)
    {
        var name = Name(kind, root, instance);
        await StopAsync(kind, root, instance);
        if (Exists(name)) await ScAsync("delete", name);
        var path = ConfigPath(name);
        if (File.Exists(path)) File.Delete(path);
        var state = Path.ChangeExtension(path, ".state.json");
        if (File.Exists(state)) File.Delete(state);
    }
    internal static Task SetStartupAsync(ComponentKind kind, string root, bool enabled, string? instance = null)
    {
        var name = Name(kind, root, instance); EnsureOwnedService(name, kind, root);
        return ScAsync("config", name, "start=", enabled ? "auto" : "demand");
    }
    internal static async Task ApplyFrpAsync(ComponentKind kind, string root, string executable, string candidate,
        string active, string? instance, string appliedIdentity)
    {
        var path = ConfigPath(Name(kind, root, instance));
        var previous = File.Exists(path) ? JsonSerializer.Deserialize<ComponentServiceConfiguration>(await File.ReadAllTextAsync(path)) : null;
        var previousContent = File.Exists(active) ? await File.ReadAllBytesAsync(active) : null;
        var wasRunning = previous is not null && Snapshot(kind, root, instance).Running;
        if (previous is not null) Validate(previous);
        await StopAsync(kind, root, instance);
        File.Move(candidate, active, true);
        try
        {
            await InstallAsync(kind, root, executable, active, instance: instance, appliedIdentity: appliedIdentity);
            await StartAsync(kind, root, instance);
        }
        catch
        {
            await StopAsync(kind, root, instance);
            if (previous is not null && previousContent is not null)
            {
                if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(previous.Executable))) != previous.BinaryHash)
                    throw new UnauthorizedAccessException("Previous component integrity failed.");
                await File.WriteAllBytesAsync(active + ".new", previousContent); File.Move(active + ".new", active, true);
                await InstallAsync(kind, root, previous.Executable, active, instance: instance, appliedIdentity: previous.AppliedIdentity);
                if (wasRunning) await StartAsync(kind, root, instance);
            }
            else await RemoveAsync(kind, root, instance);
            throw;
        }
    }
    internal static async Task RemoveFrpAsync(string root)
    {
        var prefix = Name(ComponentKind.Frps, root).Replace("-Frps-", "-Frpc-") + "-";
        if (Directory.Exists(Path.Combine(HostRoot, "services")))
            foreach (var path in Directory.EnumerateFiles(Path.Combine(HostRoot, "services"), prefix + "*.json"))
            {
                if (path.EndsWith(".state.json", StringComparison.Ordinal)) continue;
                var config = JsonSerializer.Deserialize<ComponentServiceConfiguration>(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException();
                Validate(config);
                await RemoveAsync(ComponentKind.Frpc, root, Path.GetFileName(Path.GetDirectoryName(config.Configuration)));
            }
        await RemoveAsync(ComponentKind.Frps, root);
    }
    internal static ManagedProcessSnapshot Snapshot(ComponentKind kind, string root, string? instance = null)
    {
        var name = Name(kind, root, instance);
        if (!Exists(name)) return new(false, false, false, null, []);
        using var service = new ServiceController(name);
        var serviceRunning = service.Status == ServiceControllerStatus.Running;
        EnsureOwnedService(name, kind, root);
        var path = ConfigPath(name);
        var config = JsonSerializer.Deserialize<ComponentServiceConfiguration>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        Validate(config);
        var statePath = Path.ChangeExtension(path, ".state.json");
        if (!File.Exists(statePath)) return new(false, false, false, null, []);
        using var stateInput = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var state = JsonSerializer.Deserialize<ComponentProcessState>(stateInput) ?? throw new InvalidDataException();
        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            var running = serviceRunning && !process.HasExited && process.StartTime.ToUniversalTime() == state.StartedAt
                && string.Equals(process.MainModule?.FileName, config.Executable, StringComparison.OrdinalIgnoreCase);
            return new(running, running && state.Connected, state.AuthenticationFailed, state.StartedAt, state.Logs, running ? config.AppliedIdentity : null);
        }
        catch (ArgumentException) { return new(false, false, false, null, state.Logs); }
    }
    private static bool Exists(string name)
    {
        using var service = new ServiceController(name);
        try { _ = service.Status; return true; }
        catch (InvalidOperationException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 }) { return false; }
    }
    private static void EnsureOwnedService(string name, ComponentKind kind, string root)
    {
        var path = ConfigPath(name); RequireProtected(path);
        var configuration = JsonSerializer.Deserialize<ComponentServiceConfiguration>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        Validate(configuration);
        if (configuration.Kind != kind || !string.Equals(configuration.Root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        var command = key?.GetValue("ImagePath") as string ?? throw new UnauthorizedAccessException();
        var match = System.Text.RegularExpressions.Regex.Match(command, "^\"(?<exe>[^\"]+)\" --component-service --config \"(?<config>[^\"]+)\"$");
        if (!match.Success || !string.Equals(match.Groups["config"].Value, path, StringComparison.OrdinalIgnoreCase)
            || !WindowsManagedRuntimePolicy.Contains(Path.Combine(HostRoot, "host"), match.Groups["exe"].Value)
            || Path.GetFileName(match.Groups["exe"].Value) != "RelaxKonOS.PrivilegedHelper.exe") throw new UnauthorizedAccessException();
        RequireProtected(match.Groups["exe"].Value);
    }
    private static async Task ScAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("SCM command could not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new IOException("SCM component operation failed.");
    }
    internal static void ProtectDirectory(string path)
    {
        Directory.CreateDirectory(path);
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var kind in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static void RequireProtected(string path)
    {
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
        var security = new FileInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))
            throw new UnauthorizedAccessException("Component host owner is not trusted.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0
                && rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544")) throw new UnauthorizedAccessException("Component host must be administrator-owned.");
        var directorySecurity = new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
        foreach (FileSystemAccessRule rule in directorySecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0
                && rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544")) throw new UnauthorizedAccessException("Component host directory is writable by an untrusted identity.");
    }
    protected override void OnStart(string[] args)
    {
        _job = WindowsComponentJob.Create();
        _worker = Task.Run(async () => {
        try { await SuperviseAsync(); }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch { Environment.Exit(70); }
        });
    }
    private async Task SuperviseAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var configuration = _configuration;
            if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(configuration.Executable))) != configuration.BinaryHash)
                throw new InvalidDataException("Component binary integrity failed.");
            var info = new ProcessStartInfo(configuration.Executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(configuration.Executable)!, RedirectStandardOutput = true, RedirectStandardError = true };
            var arguments = configuration.Kind switch {
                ComponentKind.Nginx => new[] { "-p", configuration.Root.Replace('\\', '/') + "/", "-c", configuration.Configuration },
                ComponentKind.Mihomo => new[] { "-d", configuration.DataDirectory!, "-f", configuration.Configuration },
                _ => new[] { "-c", configuration.Configuration }
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, value) => Record(value.Data);
            process.ErrorDataReceived += (_, value) => Record(value.Data);
            lock (_stateGate)
            {
                if (_stopping.IsCancellationRequested) return;
                if (!process.Start()) throw new IOException("Component failed to start.");
                WindowsComponentJob.Attach(_job!, process);
                _connected = _authenticationFailed = false; _child = process;
            }
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); Persist();
            await process.WaitForExitAsync();
            lock (_stateGate) { _child = null; }
            if (!_stopping.IsCancellationRequested) await Task.Delay(3000, _stopping.Token);
        }
    }
    private void Record(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        lock (_stateGate)
        {
            if (raw.Contains("login to server success", StringComparison.OrdinalIgnoreCase)) _connected = true;
            if (raw.Contains("login to server failed", StringComparison.OrdinalIgnoreCase) || raw.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)) { _authenticationFailed = true; _connected = false; }
            _logs.Enqueue(new(DateTimeOffset.UtcNow, "information", _authenticationFailed ? "Component authentication failed." : _connected ? "FRP connected." : "Component runtime activity."));
            while (_logs.Count > 200) _logs.Dequeue();
            Persist();
        }
    }
    private void Persist()
    {
        lock (_stateGate)
        {
            if (_child is not { HasExited: false } child) return;
            var state = new ComponentProcessState(child.Id, child.StartTime.ToUniversalTime(), _connected, _authenticationFailed, _logs.ToArray());
            File.WriteAllText(_statePath + ".new", JsonSerializer.Serialize(state)); File.Move(_statePath + ".new", _statePath, true);
        }
    }
    protected override void OnStop()
    {
        _stopping.Cancel();
        lock (_stateGate) { if (_child is { HasExited: false } child) child.Kill(true); }
        _worker?.GetAwaiter().GetResult();
        _job?.Dispose();
    }
}
