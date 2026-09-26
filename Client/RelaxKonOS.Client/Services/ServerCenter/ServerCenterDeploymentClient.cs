using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>Remote staging identity. Keep this with the operation id until the authoritative receipt is read.</summary>
public sealed record ServerCenterStagedOperation(Guid OperationId, HostPlatformKind Platform, string RemoteDirectory);

/// <summary>
/// Binds the signed release verifier to the built-in SSH/SFTP transport. The caller supplies trusted
/// release assets from its pinned release configuration, never from the archive being uploaded.
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
        Stream verifier,
        Stream? signedArchive,
        ServerRuntimeIdentifier? expectedRuntime,
        string? trustedKeyId,
        string? trustedPublicKeyPem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(verifier);
        if (!transport.IsConnected) throw new InvalidOperationException("A trusted SSH session is required.");
        if (!launcher.CanRead || !verifier.CanRead || !launcher.CanSeek || !verifier.CanSeek)
            throw new ArgumentException("Seekable launcher and verifier streams are required.");
        if (request.SchemaVersion != ServerDeploymentProtocol.Version || request.OperationId == Guid.Empty)
            throw new ArgumentException("The deployment request is invalid.", nameof(request));

        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, RelaxKonOSJsonOptions.Default);
        if (!ServerDeploymentRequestWireValidation.IsStrictRequest(requestBytes))
            throw new ArgumentException("The deployment request is not a strict wire request.", nameof(request));

        var needsArchive = request.Kind is ServerDeploymentKind.Install or ServerDeploymentKind.Upgrade;
        if (needsArchive)
        {
            if (signedArchive is null || !signedArchive.CanSeek ||
                expectedRuntime is null || string.IsNullOrWhiteSpace(trustedKeyId) ||
                string.IsNullOrWhiteSpace(trustedPublicKeyPem) ||
                !Regex.IsMatch(trustedKeyId, @"^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant) ||
                !ServerDeploymentInputRules.IsSafeStagedPackageName(request.Options?.StagedPackageName) ||
                !ServerDeploymentInputRules.IsSha256(request.Options?.PackageDigest))
                throw new ArgumentException("A signed, staged release and a trusted key are required.");
            if (platform == HostPlatformKind.Windows && request.Options?.Mode != ServerInstallMode.WindowsSystem ||
                platform == HostPlatformKind.Linux && request.Options?.Mode is not
                    (ServerInstallMode.LinuxSystem or ServerInstallMode.LinuxUser))
                throw new ArgumentException("The installation mode does not match the host platform.");
            if (platform == HostPlatformKind.Windows !=
                (expectedRuntime is ServerRuntimeIdentifier.WinX64 or ServerRuntimeIdentifier.WinArm64))
                throw new ArgumentException("The release RID does not match the host platform.");

            var kind = request.Options!.Mode == ServerInstallMode.LinuxUser
                ? ServerReleasePackageKind.UserServer : ServerReleasePackageKind.Server;
            var keys = new Dictionary<string, string>(StringComparer.Ordinal) { [trustedKeyId] = trustedPublicKeyPem };
            var checkedRelease = ServerReleaseArchiveVerifier.Verify(
                signedArchive, kind, expectedRuntime.Value, keys, request.Options.PackageDigest);
            if (!checkedRelease.Verified)
                throw new InvalidDataException($"{checkedRelease.ProblemCode}: release verification failed before upload.");
        }

        var directory = await CreatePrivateDirectoryAsync(platform, cancellationToken).ConfigureAwait(false);
        var staged = new ServerCenterStagedOperation(request.OperationId, platform, directory);
        await UploadFromStartAsync(verifier, staged, platform == HostPlatformKind.Windows
            ? "release-verifier.exe" : "release-verifier", cancellationToken).ConfigureAwait(false);
        await UploadFromStartAsync(launcher, staged, platform == HostPlatformKind.Windows
            ? "RelaxKonOS-Deploy.ps1" : "relaxkonos-deploy.sh", cancellationToken).ConfigureAwait(false);
        if (needsArchive)
        {
            await UploadBytesAsync(Encoding.UTF8.GetBytes(trustedPublicKeyPem!), staged, "release-public.pem", cancellationToken)
                .ConfigureAwait(false);
            await UploadBytesAsync(Encoding.ASCII.GetBytes(trustedKeyId!), staged, "release-key-id.txt", cancellationToken)
                .ConfigureAwait(false);
            await UploadFromStartAsync(signedArchive!, staged, request.Options!.StagedPackageName!, cancellationToken)
                .ConfigureAwait(false);
        }
        await UploadBytesAsync(requestBytes, staged, "request.json", cancellationToken).ConfigureAwait(false);
        if (platform == HostPlatformKind.Linux)
        {
            var chmod = await transport.RunAsync("chmod 700 '" + directory + "/release-verifier' '" +
                directory + "/relaxkonos-deploy.sh'", cancellationToken).ConfigureAwait(false);
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
        Stream verifier,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(verifier);
        if (!transport.IsConnected) throw new InvalidOperationException("A trusted SSH session is required.");
        if (!launcher.CanRead || !verifier.CanRead || !launcher.CanSeek || !verifier.CanSeek)
            throw new ArgumentException("Seekable launcher and verifier streams are required.");

        var directory = await CreatePrivateDirectoryAsync(platform, cancellationToken).ConfigureAwait(false);
        var staged = new ServerCenterStagedOperation(operationId, platform, directory);
        await UploadFromStartAsync(verifier, staged, platform == HostPlatformKind.Windows
            ? "release-verifier.exe" : "release-verifier", cancellationToken).ConfigureAwait(false);
        await UploadFromStartAsync(launcher, staged, platform == HostPlatformKind.Windows
            ? "RelaxKonOS-Deploy.ps1" : "relaxkonos-deploy.sh", cancellationToken).ConfigureAwait(false);
        if (platform == HostPlatformKind.Linux)
        {
            var chmod = await transport.RunAsync("chmod 700 '" + directory + "/release-verifier' '" +
                directory + "/relaxkonos-deploy.sh'", cancellationToken).ConfigureAwait(false);
            if (!chmod.Succeeded) throw new IOException("Unable to mark the staged deployment tools executable.");
        }
        return staged;
    }

    public async Task<ServerDeploymentOperationDto> ExecuteAsync(
        ServerCenterStagedOperation staged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        await transport.RunAsync(LauncherCommand(staged, query: false), cancellationToken)
            .ConfigureAwait(false);
        // An SSH exit status is not proof of success. Query the persistent receipt even on a nonzero exit.
        return await QueryAsync(staged, cancellationToken).ConfigureAwait(false);
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
        string fileName, CancellationToken cancellationToken)
    {
        source.Position = 0;
        await transport.UploadAsync(source, RemotePath(staged, fileName), null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UploadBytesAsync(byte[] bytes, ServerCenterStagedOperation staged,
        string fileName, CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(bytes, writable: false);
        await transport.UploadAsync(source, RemotePath(staged, fileName), null, cancellationToken)
            .ConfigureAwait(false);
    }

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
