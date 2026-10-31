using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed ModuleHost launcher. Root work is limited to its own accounts/snapshots; all module code runs in a dedicated job/token.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsModuleHostPipeServer(WindowsHelperPipeConfiguration configuration, Action<Exception> reportFailure) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _slots = new(32);
    private readonly ConcurrentDictionary<Guid, Task> _connections = new();
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    private Task? _listener;

    internal void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("ModuleHost listener is already started.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User != WindowsModuleHostSecurity.System) throw new UnauthorizedAccessException("ModuleHost requires LocalSystem Helper.");
        var policy = configuration.ModuleHosts ?? throw new InvalidOperationException("ModuleHost policy is missing.");
        policy.Validate();
        foreach (var path in new[] { policy.ExecutablePath, policy.SourceRoot, policy.IsolationRoot })
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat is not ("NTFS" or "ReFS"))
                throw new PlatformNotSupportedException("ModuleHost requires a local ACL-capable volume.");
        }
        WindowsModuleHostSecurity.RequireProtected(policy.ExecutablePath);
        var first = CreatePipe();
        _listener = ListenAsync(first);
    }
    private NamedPipeServerStream CreatePipe() => NamedPipeServerStreamAcl.Create(ModuleHostPipeProtocol.PipeName(configuration.PipeName),
        PipeDirection.InOut, 32, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough, 4096, 4096,
        WindowsPrivilegedPipeSecurity.Build(configuration.ServerServiceSid, configuration.DeveloperUserSids));

    private async Task ListenAsync(NamedPipeServerStream? first)
    {
        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var reserved = false;
            try
            {
                await _slots.WaitAsync(_stopping.Token);
                reserved = true;
                pipe = first ?? CreatePipe(); first = null;
                await pipe.WaitForConnectionAsync(_stopping.Token);
                var connected = pipe;
                var id = Guid.NewGuid();
                var task = Task.Run(async () =>
                {
                    await using (connected)
                    {
                        try { await HandleAsync(connected); }
                        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                        catch (Exception error) { reportFailure(error); }
                        finally { _slots.Release(); }
                    }
                });
                _connections[id] = task;
                _ = task.ContinueWith(completed => _connections.TryRemove(id, out _), TaskScheduler.Default);
                pipe = null;
                reserved = false;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception error) { reportFailure(error); }
            finally
            {
                if (reserved) _slots.Release();
                if (pipe is not null) await pipe.DisposeAsync();
            }
        }
        if (first is not null) await first.DisposeAsync();
    }
    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        var secret = Convert.FromBase64String(configuration.SharedSecret);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        long outbound = 0;
        using var write = new SemaphoreSlim(1, 1);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        async Task Send(string type, string payload)
        {
            await write.WaitAsync(stopping.Token);
            try
            {
                await ModuleHostPipeProtocol.WriteAsync(pipe, new(outbound++, type, payload), secret,
                    type == "hello" ? "" : nonce, ModuleHostPipeProtocol.HelperDirection, stopping.Token);
            }
            finally { write.Release(); }
        }
        await Send("hello", nonce);
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        setup.CancelAfter(TimeSpan.FromSeconds(20));
        var frame = await ModuleHostPipeProtocol.ReadAsync(pipe, secret, nonce, ModuleHostPipeProtocol.ClientDirection, 0, setup.Token);
        if (frame.Type != "launch") throw new InvalidDataException();
        var launch = ModuleHostPipeProtocol.Read<ModuleHostLaunchRequest>(Encoding.UTF8.GetBytes(frame.Payload));
        ValidateLaunch(launch);
        if (!_active.TryAdd(launch.ModuleId, 0)) throw new InvalidOperationException("ModuleHost is already active.");
        try
        {
            var prepared = Prepare(pipe, launch, setup.Token);
            using var child = new WindowsModuleHostProcess(prepared.Identity, configuration.ModuleHosts!.ExecutablePath,
                prepared.PackageRoot, prepared.DataPath, launch.ServiceToken, launch.BrokerUrl);
            var errors = Task.Run(async () =>
            {
                var buffer = new char[2048];
                while (await child.Error.ReadAsync(buffer, stopping.Token) is var read && read > 0)
                    await Send("stderr", new string(buffer, 0, read));
            });
            async Task Relay(TimeSpan timeout)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                deadline.CancelAfter(timeout);
                var response = new StringBuilder();
                var character = new char[1];
                while (await child.Output.ReadAsync(character, deadline.Token) != 0)
                {
                    if (character[0] == '\n') { await Send("stdout", response.ToString()); return; }
                    if (response.Length >= 16384) throw new InvalidDataException();
                    response.Append(character[0]);
                }
                throw new EndOfStreamException();
            }
            try
            {
                await Relay(TimeSpan.FromSeconds(20));
                long inbound = 1;
                while (!stopping.IsCancellationRequested)
                {
                    var command = await ModuleHostPipeProtocol.ReadAsync(pipe, secret, nonce,
                        ModuleHostPipeProtocol.ClientDirection, inbound++, stopping.Token);
                    if (command.Type != "command" || command.Payload.Length > 256) throw new InvalidDataException();
                    var control = ModuleHostPipeProtocol.Read<ModuleHostCommand>(Encoding.UTF8.GetBytes(command.Payload));
                    if (control.Type is not (ModuleHostProtocol.Health or ModuleHostProtocol.Shutdown)) throw new InvalidDataException();
                    await child.Input.WriteLineAsync(command.Payload);
                    await Relay(TimeSpan.FromSeconds(12));
                    if (control.Type == ModuleHostProtocol.Shutdown)
                    {
                        await child.WaitForExitAsync(stopping.Token).WaitAsync(TimeSpan.FromSeconds(12), stopping.Token);
                        await Send("exit", child.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    }
                }
            }
            finally
            {
                stopping.Cancel();
                // Kill the complete job before draining: detached children cannot retain handles or outlive the channel.
                child.Dispose();
                try { await errors; } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        catch (Exception)
        {
            if (!stopping.IsCancellationRequested) try { await Send("faulted", "module-host-isolation-failed"); } catch (IOException) { }
            throw;
        }
        finally
        {
            _active.TryRemove(launch.ModuleId, out _);
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private sealed record Prepared(WindowsModuleHostAccount.Identity Identity, string PackageRoot, string DataPath);
    private Prepared Prepare(NamedPipeServerStream pipe, ModuleHostLaunchRequest launch, CancellationToken cancellationToken)
    {
        var policy = configuration.ModuleHosts!;
        var relative = Path.Combine(launch.ModuleId, launch.Version);
        byte[] ReadSource(string name, long maximum)
        {
            byte[]? result = null;
            var path = Path.Combine(policy.SourceRoot, relative, name);
            // Read ingress using the authenticated Server's token, never LocalSystem. Even a reparse
            // race cannot turn copying a package into a root-only file read.
            pipe.RunAsClient(() =>
            {
                WindowsManagedRuntimePolicy.RequireNoLinks(path);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length < 0 || input.Length > maximum) throw new InvalidDataException();
                result = new byte[checked((int)input.Length)];
                input.ReadExactly(result);
                if (input.ReadByte() != -1) throw new InvalidDataException();
            });
            return result ?? throw new IOException();
        }
        var manifestBytes = ReadSource("manifest.json", 4 * 1024 * 1024);
        if (Hash(manifestBytes) != launch.ManifestSha256) throw new InvalidDataException();
        var manifest = ModulePackageJson.Read<ModulePackageManifest>(manifestBytes);
        ModulePackageVerifier.ValidateManifest(manifest);
        if (manifest.Runtime != "module-host" || manifest.ModuleId != launch.ModuleId || manifest.Version != launch.Version
            || manifest.Rid != RuntimeInformation.RuntimeIdentifier) throw new InvalidDataException();
        var runtime = Path.GetDirectoryName(policy.ExecutablePath)!;
        WindowsModuleHostSecurity.RequireProtected(policy.ExecutablePath);
        WindowsModuleHostSecurity.CreateProtectedDirectory(policy.IsolationRoot);
        var moduleRoot = Path.Combine(policy.IsolationRoot, launch.ModuleId);
        if (!Directory.Exists(moduleRoot) && Directory.EnumerateDirectories(policy.IsolationRoot).Take(128).Count() >= 128)
            throw new InvalidDataException();
        WindowsModuleHostSecurity.CreateProtectedDirectory(moduleRoot);
        var identity = WindowsModuleHostAccount.Ensure(moduleRoot, launch.ModuleId, policy.IsolationRoot);
        var sid = new SecurityIdentifier(identity.Sid);
        WindowsModuleHostSecurity.GrantRuntimeRead(runtime, sid);
        var package = Path.Combine(moduleRoot, launch.Version + "-" + launch.ManifestSha256);
        if (!Directory.Exists(package) && Directory.EnumerateDirectories(moduleRoot).Take(18).Count() >= 18)
            throw new InvalidDataException();
        var staging = Path.Combine(moduleRoot, ".prepare-" + Guid.NewGuid().ToString("N"));
        WindowsModuleHostSecurity.CreateProtectedDirectory(staging);
        try
        {
            File.WriteAllBytes(Path.Combine(staging, "manifest.json"), manifestBytes);
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var inputPath = Path.Combine(policy.SourceRoot, relative, file.Path);
                pipe.RunAsClient(() =>
                {
                    WindowsManagedRuntimePolicy.RequireNoLinks(inputPath);
                    using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long length = 0;
                    int read;
                    while ((read = input.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        length += read;
                        if (length > file.Length) throw new InvalidDataException();
                        hash.AppendData(buffer, 0, read);
                        // This output handle was opened by System at a fixed private staging path;
                        // the authenticated caller can supply bytes, never choose a root destination.
                        output.Write(buffer, 0, read);
                    }
                    if (length != file.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                        throw new InvalidDataException();
                });
                output.Flush(true);
            }
            new DirectoryInfo(staging).SetAccessControl(WindowsModuleHostSecurity.DirectoryAcl(sid, System.Security.AccessControl.FileSystemRights.ReadAndExecute));
            // Existing children inherited the System/Admin ACL before the module read grant was added.
            WindowsModuleHostSecurity.GrantRuntimeRead(staging, sid);
            RequireChild(moduleRoot, staging); RequireChild(moduleRoot, package);
            if (!Directory.Exists(package)) Directory.Move(staging, package);
            else WindowsModuleHostSecurity.RequireProtected(package);
        }
        finally { if (Directory.Exists(staging)) { RequireChild(moduleRoot, staging); Directory.Delete(staging, recursive: true); } }
        var data = Path.Combine(moduleRoot, "data");
        if (!Directory.Exists(data)) new DirectoryInfo(data).Create(WindowsModuleHostSecurity.DirectoryAcl(sid, System.Security.AccessControl.FileSystemRights.Modify));
        WindowsModuleHostSecurity.RequirePrivateData(data, sid);
        WindowsManagedRuntimePolicy.RequireNoLinks(Path.Combine(data, "temp"));
        Directory.CreateDirectory(Path.Combine(data, "temp"));
        return new(identity, package, data);
    }
    private static void RequireChild(string root, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("ModuleHost destination escaped its private root.");
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void ValidateLaunch(ModuleHostLaunchRequest request)
    {
        if (request.LaunchId == Guid.Empty || request.ModuleId is not { Length: > 0 and <= 96 }
            || request.ModuleId[0] is < 'a' or > 'z' || request.ModuleId.EndsWith('.')
            || !request.ModuleId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')
            || !Version.TryParse(request.Version, out var version) || version.Build < 0 || version.Revision != -1 || version.ToString(3) != request.Version
            || request.ManifestSha256 is not { Length: 64 } || !request.ManifestSha256.All(char.IsAsciiHexDigitLower)
            || request.ServiceToken is not { Length: 64 } || !request.ServiceToken.All(char.IsAsciiHexDigitLower)
            || request.BrokerUrl is not null && (!Uri.TryCreate(request.BrokerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0)) throw new InvalidDataException();
    }
    public async Task StopAsync()
    {
        _stopping.Cancel();
        if (_listener is not null) try { await _listener; } catch (OperationCanceledException) { }
        await Task.WhenAll(_connections.Values);
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose(); _slots.Dispose();
    }
}
