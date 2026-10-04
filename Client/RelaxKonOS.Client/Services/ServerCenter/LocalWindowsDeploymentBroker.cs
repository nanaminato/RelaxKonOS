using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>One UAC child per local deployment session; credentials and payloads travel over a private, PID-checked pipe.</summary>
[SupportedOSPlatform("windows")]
public sealed class LocalWindowsDeploymentSessionFactory : ILocalWindowsDeploymentSessionFactory
{
    public async Task<ILocalWindowsDeploymentSession> OpenAsync(CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !Path.GetFileName(executable).Equals("RelaxKonOS.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local deployment requires the RelaxKonOS desktop executable.");
        var pipeName = LocalWindowsDeploymentBroker.PipePrefix + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 65536, 65536, security);
        Process? child = null;
        try
        {
            // ShellExecute is the UAC boundary. Only an executable, a random pipe name and our PID enter the command line.
            child = await Task.Run(() => Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"{LocalWindowsDeploymentBroker.Switch} {pipeName} {Environment.ProcessId}"
            }) ?? throw new IOException("Unable to start the deployment broker."), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = child.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited && !pipe.IsConnected)
                throw new IOException("The deployment broker exited before connecting.");
            await connected.ConfigureAwait(false);
            if (!LocalWindowsDeploymentBroker.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != child.Id)
                throw new UnauthorizedAccessException("Unexpected deployment pipe client.");
            var hello = await LocalDeploymentWire.ReadJsonAsync<LocalDeploymentMessage>(pipe, timeout.Token).ConfigureAwait(false);
            if (hello.Kind != "ready") throw new InvalidDataException("Invalid deployment broker handshake.");
            return new LocalWindowsDeploymentSession(pipe, child);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            child?.Dispose();
            throw;
        }
    }
}

internal sealed class LocalWindowsDeploymentSession(NamedPipeServerStream pipe, Process child) : ILocalWindowsDeploymentSession
{
    public async Task<ServerDeploymentOperationDto> ExecuteAsync(ServerDeploymentRequest request, Stream? archive,
        Stream? certificate, string? certificatePassword, IProgress<ServerDeploymentTransfer?>? transfer, CancellationToken ct)
    {
        LocalWindowsDeploymentBroker.ValidateRequest(request);
        await LocalDeploymentWire.WriteJsonAsync(pipe, request, ct).ConfigureAwait(false);
        await LocalDeploymentWire.WriteFileAsync(pipe, archive, LocalDeploymentWire.MaximumArchiveBytes, ct).ConfigureAwait(false);
        await LocalDeploymentWire.WriteFileAsync(pipe, certificate, LocalDeploymentWire.MaximumCertificateBytes, ct).ConfigureAwait(false);
        await LocalDeploymentWire.WriteJsonAsync(pipe, certificate is null ? "" : certificatePassword ?? "", ct).ConfigureAwait(false);
        while (true)
        {
            var message = await LocalDeploymentWire.ReadJsonAsync<LocalDeploymentMessage>(pipe, ct).ConfigureAwait(false);
            if (message.Kind == "transfer") { transfer?.Report(message.Transfer); continue; }
            if (message.Kind == "error") throw new IOException("The local deployment broker failed (" + message.Error + ").");
            if (message.Kind != "receipt" || message.Receipt is null) throw new InvalidDataException("Invalid broker response.");
            var receipt = ServerDeploymentRecordReader.ReadRecord(message.Receipt, request.OperationId);
            if (receipt.Kind != request.Kind) throw new InvalidDataException("Unexpected deployment receipt kind.");
            transfer?.Report(null);
            return receipt;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the pipe ends the broker after its current engine operation finishes; do not kill a service transition.
        await pipe.DisposeAsync().ConfigureAwait(false);
        child.Dispose();
    }
}

/// <summary>Called before Avalonia initializes. It accepts fixed deployment requests, never client-provided commands or launchers.</summary>
public static class LocalWindowsDeploymentBroker
{
    public const string Switch = "--local-windows-deployment";
    internal const string PipePrefix = "relaxkonos-local-deploy-";

    public static bool IsBrokerInvocation(string[] args) => args.Length > 0 && args[0] == Switch;

    [SupportedOSPlatform("windows")]
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || !args[1].StartsWith(PipePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1][PipePrefix.Length..], "N", out _) ||
            !int.TryParse(args[2], out var parentPid) || parentPid <= 0 ||
            !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(30000).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != parentPid) return 3;
            await LocalDeploymentWire.WriteJsonAsync(pipe, new LocalDeploymentMessage("ready"), CancellationToken.None).ConfigureAwait(false);
            while (pipe.IsConnected)
            {
                ServerDeploymentRequest request;
                using var idle = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try { request = await LocalDeploymentWire.ReadJsonAsync<ServerDeploymentRequest>(pipe, idle.Token).ConfigureAwait(false); }
                catch (EndOfStreamException) { break; }
                ValidateRequest(request);
                await ExecuteAsync(pipe, request).ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception error)
        {
            try { await LocalDeploymentWire.WriteJsonAsync(pipe, new LocalDeploymentMessage("error", Error: error.GetType().Name), CancellationToken.None).ConfigureAwait(false); }
            catch (IOException) { }
            return 1;
        }
    }

    public static void ValidateRequest(ServerDeploymentRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, RelaxKonOSJsonOptions.Default);
        if (request.SchemaVersion != ServerDeploymentProtocol.Version || request.OperationId == Guid.Empty ||
            !ServerDeploymentRequestWireValidation.IsStrictRequest(bytes) ||
            !Enum.IsDefined(request.Kind) ||
            request.Options is { } options && (options.Mode != ServerInstallMode.WindowsSystem ||
                options.Source == ServerPackageSourceKind.RemoteBundle || options.RemotePackagePath is not null ||
                options.Retention == ServerDataRetention.Delete && request.Kind != ServerDeploymentKind.Uninstall))
            throw new InvalidDataException("Invalid local deployment request.");
        if (request.Kind is ServerDeploymentKind.Install or ServerDeploymentKind.Upgrade &&
            request.Options is not { Confirmed: true, Mode: ServerInstallMode.WindowsSystem })
            throw new InvalidDataException("An explicitly reviewed Windows installation is required.");
        if (request.Kind is ServerDeploymentKind.Upgrade or ServerDeploymentKind.Repair or ServerDeploymentKind.Rollback or ServerDeploymentKind.Uninstall &&
            (request.Options is not { Confirmed: true, Mode: ServerInstallMode.WindowsSystem } ||
             !ServerInstallationId.IsValid(request.Options.ExpectedInstallationId)))
            throw new InvalidDataException("A reviewed lifecycle action bound to the installed identity is required.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task ExecuteAsync(Stream pipe, ServerDeploymentRequest request)
    {
        // A user TEMP parent allows its owner to rename children. Put staging below an administrator-owned
        // random container in ProgramData, while retaining the invoking-account owner required by the launcher.
        var container = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "relaxkonos-local-deploy-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(container, "relaxkonos-deploy-" + Guid.NewGuid().ToString("N"));
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(container).Create(acl);
        try
        {
            acl.SetOwner(WindowsIdentity.GetCurrent().User!);
            new DirectoryInfo(directory).Create(acl);
            using var inputTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var hasArchive = await LocalDeploymentWire.ReadFileAsync(pipe, Path.Combine(directory, "server.zip"),
                LocalDeploymentWire.MaximumArchiveBytes, inputTimeout.Token).ConfigureAwait(false);
            var hasCertificate = await LocalDeploymentWire.ReadFileAsync(pipe, Path.Combine(directory, "certificate.pfx"),
                LocalDeploymentWire.MaximumCertificateBytes, inputTimeout.Token).ConfigureAwait(false);
            var password = await LocalDeploymentWire.ReadJsonAsync<string>(pipe, inputTimeout.Token).ConfigureAwait(false);
            var installing = request.Kind is ServerDeploymentKind.Install or ServerDeploymentKind.Upgrade;
            if (hasArchive != (installing && request.Options?.Source == ServerPackageSourceKind.LocalBundle) ||
                hasArchive && request.Options?.StagedPackageName != "server.zip" ||
                hasCertificate != (installing && request.Options?.CertificateMode == ServerCertificateMode.Custom) ||
                !hasCertificate && password.Length != 0 || password.Length > 4096)
                throw new InvalidDataException("Unexpected staged installation files.");
            if (hasCertificate) await File.WriteAllTextAsync(Path.Combine(directory, "certificate-password.txt"), password, new UTF8Encoding(false)).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(directory, "request.json"),
                JsonSerializer.SerializeToUtf8Bytes(request, RelaxKonOSJsonOptions.Default)).ConfigureAwait(false);
            var tools = await new FileServerCenterReleaseSource().ResolveToolsAsync(HostPlatformKind.Windows).ConfigureAwait(false);
            await using (var launcher = tools!.OpenLauncher())
            await using (var destination = File.Create(Path.Combine(directory, tools.LauncherFileName)))
                await launcher.CopyToAsync(destination).ConfigureAwait(false);

            // The launcher handles validation, official downloads, locking, rollback and persistent receipts.
            await RunLauncherAsync(directory, null, pipe, request.OperationId).ConfigureAwait(false);
            var receiptJson = await RunLauncherAsync(directory, request.OperationId, null, request.OperationId).ConfigureAwait(false);
            _ = ServerDeploymentRecordReader.ReadRecord(receiptJson.Trim(), request.OperationId);
            await LocalDeploymentWire.WriteJsonAsync(pipe, new LocalDeploymentMessage("receipt", Receipt: receiptJson.Trim()), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // Only this generated temporary directory is removed; the engine's persistent journal and installation stay intact.
            try { Directory.Delete(container, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string> RunLauncherAsync(string directory, Guid? query, Stream? pipe, Guid operationId)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(directory, "RelaxKonOS-Deploy.ps1") })
            start.ArgumentList.Add(arg);
        if (query.HasValue) { start.ArgumentList.Add("-QueryOperationId"); start.ArgumentList.Add(query.Value.ToString()); }
        using var process = Process.Start(start) ?? throw new IOException("Unable to start the embedded deployment launcher.");
        var output = process.StandardOutput.ReadToEndAsync();
        // Drain stderr without exposing raw commands, paths or credentials in the UI/pipe.
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var wait = process.WaitForExitAsync();
        var disconnected = false;
        while (!wait.IsCompleted)
        {
            await Task.WhenAny(wait, Task.Delay(750)).ConfigureAwait(false);
            if (pipe is null || disconnected) continue;
            ServerDeploymentTransfer? transfer = null;
            try
            {
                var progressPath = Path.Combine(directory, "transfer.json");
                if (File.Exists(progressPath)) transfer = ServerCenterDeploymentClient.ReadTransfer(await File.ReadAllTextAsync(progressPath).ConfigureAwait(false), operationId);
            }
            catch (Exception) { /* Progress is advisory; never abandon a running engine due to malformed progress. */ }
            try { await LocalDeploymentWire.WriteJsonAsync(pipe, new LocalDeploymentMessage("transfer", Transfer: transfer), CancellationToken.None).ConfigureAwait(false); }
            catch (IOException) { disconnected = true; }
        }
        await wait.ConfigureAwait(false);
        await errors.ConfigureAwait(false);
        var text = await output.ConfigureAwait(false);
        // Installation exit codes are not authoritative. Query the receipt after any engine exit.
        if (query.HasValue && process.ExitCode != 0) throw new IOException("Unable to query the local deployment receipt.");
        return text;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}

internal sealed record LocalDeploymentMessage(string Kind, string? Receipt = null, ServerDeploymentTransfer? Transfer = null, string? Error = null);

internal static class LocalDeploymentWire
{
    public const long MaximumArchiveBytes = 8_589_934_592L;
    public const long MaximumCertificateBytes = 16_777_216;
    private const int MaximumJsonBytes = 262144;

    public static async Task WriteJsonAsync<T>(Stream destination, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RelaxKonOSJsonOptions.Default);
        if (bytes.Length > MaximumJsonBytes) throw new InvalidDataException("Oversized local deployment message.");
        await WriteLengthAsync(destination, bytes.Length, ct).ConfigureAwait(false);
        await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadJsonAsync<T>(Stream source, CancellationToken ct)
    {
        var length = await ReadLengthAsync(source, ct).ConfigureAwait(false);
        if (length is <= 0 or > MaximumJsonBytes) throw new InvalidDataException("Invalid local deployment message size.");
        var bytes = new byte[(int)length];
        await source.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (typeof(T) == typeof(ServerDeploymentRequest) && !ServerDeploymentRequestWireValidation.IsStrictRequest(bytes))
            throw new InvalidDataException("Invalid deployment request wire format.");
        return JsonSerializer.Deserialize<T>(bytes, RelaxKonOSJsonOptions.Default) ?? throw new InvalidDataException("Missing message.");
    }

    public static async Task WriteFileAsync(Stream destination, Stream? file, long maximumBytes, CancellationToken ct)
    {
        var length = file is null ? -1 : file.Length - file.Position;
        if (length < -1 || length > maximumBytes) throw new InvalidDataException("Invalid local deployment file size.");
        await WriteLengthAsync(destination, length, ct).ConfigureAwait(false);
        if (file is not null) await CopyExactlyAsync(file, destination, length, ct).ConfigureAwait(false);
    }

    public static async Task<bool> ReadFileAsync(Stream source, string path, long maximumBytes, CancellationToken ct)
    {
        var length = await ReadLengthAsync(source, ct).ConfigureAwait(false);
        if (length == -1) return false;
        if (length < 0 || length > maximumBytes) throw new InvalidDataException("Invalid local deployment file size.");
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await CopyExactlyAsync(source, destination, length, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (length > 0)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), ct).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            length -= count;
        }
    }

    private static async Task WriteLengthAsync(Stream stream, long value, CancellationToken ct)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task<long> ReadLengthAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[8];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }
}
