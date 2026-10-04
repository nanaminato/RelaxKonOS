using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>Remote staging identity. Keep this with the operation id until the authoritative receipt is read.</summary>
public sealed record ServerCenterStagedOperation(Guid OperationId, HostPlatformKind Platform, string RemoteDirectory);
public sealed record ServerDeploymentTransfer(long Bytes, long? Total);

/// <summary>
/// Stages the embedded launcher and, for local sources, the user ZIP through SSH/SFTP.
/// </summary>
public sealed class ServerCenterDeploymentClient(IServerCenterSshTransport transport)
{
    private static readonly Regex LinuxStagingPath = new(
        @"^/tmp/relaxkonos-deploy\.[A-Za-z0-9]{8,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex WindowsStagingPath = new(
        @"^[A-Za-z]:[\\/].*[\\/]relaxkonos-deploy-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public async Task<ServerCenterStagedOperation> StageAsync(
        ServerDeploymentRequest request,
        HostPlatformKind platform,
        Stream launcher,
        Stream? archive,
        ServerRuntimeIdentifier? expectedRuntime,
        Stream? certificate,
        string? certificatePassword,
        CancellationToken cancellationToken,
        IProgress<double>? uploadProgress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(launcher);
        if (!transport.IsConnected) throw new InvalidOperationException("A trusted SSH session is required.");
        if (!launcher.CanRead || !launcher.CanSeek)
            throw new ArgumentException("Seekable launcher streams are required.");
        if (request.SchemaVersion != ServerDeploymentProtocol.Version || request.OperationId == Guid.Empty)
            throw new ArgumentException("The deployment request is invalid.", nameof(request));

        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, RelaxKonOSJsonOptions.Default);
        if (!ServerDeploymentRequestWireValidation.IsStrictRequest(requestBytes))
            throw new ArgumentException("The deployment request is not a strict wire request.", nameof(request));

        var installing = request.Kind is ServerDeploymentKind.Install or ServerDeploymentKind.Upgrade;
        var needsArchive = installing && request.Options?.Source == ServerPackageSourceKind.LocalBundle;
        var needsCertificate = installing && request.Options?.CertificateMode == ServerCertificateMode.Custom;
        if (installing)
        {
            var options = request.Options ?? throw new ArgumentException("Deployment options are required.");
            if (options.Mode is null || expectedRuntime is null)
                throw new ArgumentException("Installation mode and target runtime are required.");
            if (needsArchive && (archive is null || !archive.CanRead || !archive.CanSeek ||
                !ServerDeploymentInputRules.IsSafeStagedPackageName(options.StagedPackageName)))
                throw new ArgumentException("A readable local ZIP is required.");
            if (options.Source == ServerPackageSourceKind.RemoteBundle &&
                (string.IsNullOrWhiteSpace(options.RemotePackagePath) ||
                 !options.RemotePackagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                 options.RemotePackagePath.Any(char.IsControl)))
                throw new ArgumentException("A server ZIP path is required.");
            if (options.Source == ServerPackageSourceKind.DirectUrl &&
                (!Uri.TryCreate(options.PackageUri, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                 !ServerDeploymentInputRules.IsSha256(options.PackageDigest)))
                throw new ArgumentException("A custom HTTPS URL and SHA-256 are required.");
            if (needsCertificate && (certificate is null || !certificate.CanRead || !certificate.CanSeek))
                throw new ArgumentException("A readable certificate is required for custom TLS.");
            if (platform == HostPlatformKind.Windows && options.Mode != ServerInstallMode.WindowsSystem ||
                platform == HostPlatformKind.Linux && options.Mode is not (ServerInstallMode.LinuxSystem or ServerInstallMode.LinuxUser))
                throw new ArgumentException("The installation mode does not match the host platform.");
            if (platform == HostPlatformKind.Windows !=
                (expectedRuntime is ServerRuntimeIdentifier.WinX64 or ServerRuntimeIdentifier.WinArm64))
                throw new ArgumentException("The runtime does not match the host platform.");
        }

        var directory = await CreatePrivateDirectoryAsync(platform, cancellationToken).ConfigureAwait(false);
        var staged = new ServerCenterStagedOperation(request.OperationId, platform, directory);
        await UploadFromStartAsync(launcher, staged, platform == HostPlatformKind.Windows
            ? "RelaxKonOS-Deploy.ps1" : "relaxkonos-deploy.sh", cancellationToken).ConfigureAwait(false);
        if (needsArchive)
        {
            await UploadFromStartAsync(archive!, staged, request.Options!.StagedPackageName!, cancellationToken, uploadProgress)
                .ConfigureAwait(false);
        }
        if (needsCertificate)
        {
            await UploadFromStartAsync(certificate!, staged, "certificate.pfx", cancellationToken).ConfigureAwait(false);
            await UploadBytesAsync(Encoding.UTF8.GetBytes(certificatePassword ?? string.Empty), staged,
                "certificate-password.txt", cancellationToken).ConfigureAwait(false);
        }
        await UploadBytesAsync(requestBytes, staged, "request.json", cancellationToken).ConfigureAwait(false);
        if (platform == HostPlatformKind.Linux)
        {
            var chmod = await transport.RunAsync("chmod 700 '" + directory + "/relaxkonos-deploy.sh'", cancellationToken).ConfigureAwait(false);
            if (!chmod.Succeeded) throw new IOException("Unable to mark the staged deployment tools executable.");
        }
        return staged;
    }

    /// <summary>
    /// Stages only the fixed query tools for reconnecting to an existing operation receipt.  It does
    /// not create a deployment request, upload a package, or start a new operation: the supplied ID
    /// is solely the immutable key of a previously recorded remote operation.
    /// </summary>
    public async Task<ServerCenterStagedOperation> StageQueryAsync(
        Guid operationId,
        HostPlatformKind platform,
        Stream launcher,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(launcher);
        if (!transport.IsConnected) throw new InvalidOperationException("A trusted SSH session is required.");
        if (!launcher.CanRead || !launcher.CanSeek)
            throw new ArgumentException("Seekable launcher streams are required.");

        var directory = await CreatePrivateDirectoryAsync(platform, cancellationToken).ConfigureAwait(false);
        var staged = new ServerCenterStagedOperation(operationId, platform, directory);
        await UploadFromStartAsync(launcher, staged, platform == HostPlatformKind.Windows
            ? "RelaxKonOS-Deploy.ps1" : "relaxkonos-deploy.sh", cancellationToken).ConfigureAwait(false);
        if (platform == HostPlatformKind.Linux)
        {
            var chmod = await transport.RunAsync("chmod 700 '" + directory + "/relaxkonos-deploy.sh'", cancellationToken).ConfigureAwait(false);
            if (!chmod.Succeeded) throw new IOException("Unable to mark the staged deployment tools executable.");
        }
        return staged;
    }

    public async Task<ServerDeploymentOperationDto> ExecuteAsync(
        ServerCenterStagedOperation staged, CancellationToken cancellationToken, string? sudoPassword = null,
        IProgress<ServerDeploymentTransfer?>? transferProgress = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        using var pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var polling = transferProgress is null ? Task.CompletedTask : PollTransferAsync(staged, transferProgress, pollingCancellation.Token);
        try
        {
            if (sudoPassword is not null)
            {
                if (staged.Platform != HostPlatformKind.Linux || sudoPassword.Any(c => c is '\r' or '\n'))
                    throw new ArgumentException("A single-line Linux sudo password is required.");
                await transport.RunWithInputAsync(LauncherCommand(staged, query: false) + "-with-sudo",
                    sudoPassword, cancellationToken).ConfigureAwait(false);
            }
            else
                await transport.RunAsync(LauncherCommand(staged, query: false), cancellationToken).ConfigureAwait(false);
            // An SSH exit status is not proof of success. Query the persistent receipt even on a nonzero exit.
            return await QueryAsync(staged, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await pollingCancellation.CancelAsync().ConfigureAwait(false);
            await polling.ConfigureAwait(false);
            transferProgress?.Report(null);
        }
    }

    private async Task PollTransferAsync(ServerCenterStagedOperation staged, IProgress<ServerDeploymentTransfer?> progress, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Validate the staging identity before constructing another fixed command.
                _ = LauncherCommand(staged, query: true);
                var path = RemotePath(staged, "transfer.json");
                var script = "$p='" + path.Replace("'", "''", StringComparison.Ordinal) +
                    "';if(Test-Path -LiteralPath $p){[Console]::Write([IO.File]::ReadAllText($p))}";
                var command = staged.Platform == HostPlatformKind.Linux
                    ? $"if [ -f '{path}' ]; then head -c 1024 '{path}'; fi"
                    : "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var result = await transport.RunAsync(command, timeout.Token).ConfigureAwait(false);
                if (result.Succeeded)
                    progress.Report(string.IsNullOrWhiteSpace(result.StandardOutput) ? null : ReadTransfer(result.StandardOutput.Trim().TrimStart('\uFEFF'), staged.OperationId));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { /* Progress is advisory; the operation receipt remains authoritative. */ }
            try { await Task.Delay(750, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static ServerDeploymentTransfer? ReadTransfer(string json, Guid operationId)
    {
        if (json.Length > 1024) throw new InvalidDataException("Transfer record is too large.");
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement;
        if (fields.EnumerateObject().Count() != 4 || fields.GetProperty("operationId").GetGuid() != operationId)
            throw new InvalidDataException("Invalid transfer identity.");
        var bytes = fields.GetProperty("bytes").GetInt64();
        long? total = fields.GetProperty("total").ValueKind == JsonValueKind.Null ? null : fields.GetProperty("total").GetInt64();
        if (bytes is < 0 or > 8_589_934_592L || total is < 0 or > 8_589_934_592L)
            throw new InvalidDataException("Invalid transfer size.");
        return fields.GetProperty("active").GetBoolean() ? new(bytes, total) : null;
    }

    public async Task<ServerDeploymentOperationDto> QueryAsync(
        ServerCenterStagedOperation staged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var result = await transport.RunAsync(LauncherCommand(staged, query: true), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException("The remote operation receipt could not be read.");
        return ServerDeploymentRecordReader.ReadRecord(result.StandardOutput.Trim(), staged.OperationId);
    }

    public async Task<string> ReadDiagnosticsAsync(ServerCenterStagedOperation staged, CancellationToken cancellationToken)
    {
        var command = LauncherCommand(staged, query: true);
        if (staged.Platform == HostPlatformKind.Linux)
            command = command.Replace(" --query ", " --diagnostics ", StringComparison.Ordinal);
        else
        {
            var encoded = command[(command.LastIndexOf(' ') + 1)..];
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded))
                .Replace(" -QueryOperationId ", " -DiagnosticsOperationId ", StringComparison.Ordinal);
            command = "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }
        var result = await transport.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException($"Deployment diagnostics query failed (exit {result.ExitStatus}).");
        var text = result.StandardOutput;
        text = Regex.Replace(text, @"(?im)(password|secret|token|authorization)(\s*[:=]\s*)[^\r\n]+", "$1$2[redacted]");
        return text.Length > 65536 ? text[..65536] : text;
    }

    private async Task<string> CreatePrivateDirectoryAsync(HostPlatformKind platform, CancellationToken cancellationToken)
    {
        string command;
        if (platform == HostPlatformKind.Linux)
        {
            command = "umask 077; mktemp -d -p /tmp relaxkonos-deploy.XXXXXXXX";
        }
        else if (platform == HostPlatformKind.Windows)
        {
            const string script = "$p=Join-Path $env:TEMP ('relaxkonos-deploy-'+[guid]::NewGuid().ToString('N'));" +
                "[IO.Directory]::CreateDirectory($p)|Out-Null;" +
                "$u=[Security.Principal.WindowsIdentity]::GetCurrent().Name;" +
                "& icacls $p /inheritance:r /grant:r ($u+':(OI)(CI)F') 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' *> $null;" +
                "if($LASTEXITCODE -ne 0){exit 1};" +
                "& icacls $p /setowner $u *> $null;" +
                "if($LASTEXITCODE -ne 0){exit 1};[Console]::WriteLine($p)";
            command = "powershell.exe -NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }
        else throw new ArgumentOutOfRangeException(nameof(platform));

        var result = await transport.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException("The remote private staging directory could not be created.");
        var path = result.StandardOutput.Trim();
        if (path.Contains('\n') || path.Contains('\r') ||
            !(platform == HostPlatformKind.Linux ? LinuxStagingPath : WindowsStagingPath).IsMatch(path))
            throw new InvalidDataException("The remote staging path is invalid.");
        return path;
    }

    private async Task UploadFromStartAsync(Stream source, ServerCenterStagedOperation staged,
        string fileName, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        source.Position = 0;
        await transport.UploadAsync(source, SftpPath(staged, fileName), progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UploadBytesAsync(byte[] bytes, ServerCenterStagedOperation staged,
        string fileName, CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(bytes, writable: false);
        await transport.UploadAsync(source, SftpPath(staged, fileName), null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SftpPath(ServerCenterStagedOperation staged, string fileName) =>
        (staged.Platform == HostPlatformKind.Windows ? "/" : "") + RemotePath(staged, fileName);

    private static string RemotePath(ServerCenterStagedOperation staged, string fileName) =>
        staged.RemoteDirectory.TrimEnd('/', '\\').Replace('\\', '/') + '/' + fileName;

    private static string LauncherCommand(ServerCenterStagedOperation staged, bool query)
    {
        if (staged.Platform == HostPlatformKind.Linux)
        {
            if (!LinuxStagingPath.IsMatch(staged.RemoteDirectory)) throw new ArgumentException("Invalid staging directory.");
            var action = query ? " --query " + staged.OperationId.ToString("D") : " --run";
            return "bash '" + staged.RemoteDirectory + "/relaxkonos-deploy.sh'" + action;
        }
        if (staged.Platform != HostPlatformKind.Windows || !WindowsStagingPath.IsMatch(staged.RemoteDirectory))
            throw new ArgumentException("Invalid staging directory.");
        var scriptPath = staged.RemoteDirectory.TrimEnd('\\', '/') + "\\RelaxKonOS-Deploy.ps1";
        var escapedPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var command = "& '" + escapedPath + "'" +
            (query ? " -QueryOperationId '" + staged.OperationId.ToString("D") + "'" : "");
        return "powershell.exe -NoProfile -NonInteractive -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }
}
