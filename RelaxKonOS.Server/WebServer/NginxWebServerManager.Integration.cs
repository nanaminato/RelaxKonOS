using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.WebServer;


internal sealed partial class NginxWebServerManager
{
    private async Task<WebServerDto?> DetectAsync(string executable, WebServerManagementMode? forcedMode, CancellationToken cancellationToken)
    {
        var details = await RunNginxAsync(executable, ["-V"], cancellationToken);
        if (!details.Success && string.IsNullOrWhiteSpace(details.Output)) return null;
        var configPath = forcedMode == WebServerManagementMode.Managed
            ? GetManagedLayout().ConfigurationPath
            : ParseConfigPath(details.Output, executable);
        var version = VersionPattern().Match(details.Output) is { Success: true } match ? match.Groups["version"].Value : null;
        var includeDirectory = configPath is null ? null : FindOwnedIncludeDirectory(configPath);
        var ownedPath = includeDirectory is null ? null : Path.Combine(includeDirectory, OwnedFileName);
        var integrated = ownedPath is not null && IsOwnedFile(ownedPath);
        if (forcedMode is null && !integrated) return null;
        var mode = forcedMode ?? WebServerManagementMode.Integrated;
        var isManaged = mode == WebServerManagementMode.Managed;
        var capabilities = new WebServerCapabilities(
            CanRead: true,
            CanTestConfiguration: true,
            CanReload: true,
            CanStart: isManaged,
            CanStop: isManaged,
            CanRestart: isManaged,
            CanUninstall: CanUninstallInstallation(isManaged, CanUseBuiltInInstaller(), integrated, executable, configPath));
        var instance = new WebServerDto(InstanceId(executable), ProviderKey, WebServerType.Nginx, mode, executable, configPath, version, DateTimeOffset.UtcNow, capabilities);
        await metadata.UpsertInstanceAsync(instance, cancellationToken);
        return instance;
    }

    private async Task<WebServerIntegrationCandidateDto?> DetectIntegrationCandidateAsync(string executable, CancellationToken cancellationToken)
    {
        var details = await RunNginxAsync(executable, ["-V"], cancellationToken);
        if (!details.Success && string.IsNullOrWhiteSpace(details.Output)) return null;
        var configPath = ParseConfigPath(details.Output, executable);
        var includeDirectory = configPath is null ? null : FindOwnedIncludeDirectory(configPath);
        if (configPath is null || includeDirectory is null) return null;
        var ownedPath = Path.Combine(includeDirectory, OwnedFileName);
        if (IsOwnedFile(ownedPath)) return null;
        var version = VersionPattern().Match(details.Output) is { Success: true } match ? match.Groups["version"].Value : null;
        return new WebServerIntegrationCandidateDto(InstanceId(executable), ProviderKey, WebServerType.Nginx,
            executable, configPath, version, DateTimeOffset.UtcNow);
    }

    private static WebServerDto ToIntegratedInstance(WebServerIntegrationCandidateDto candidate) => new(
        candidate.Id, candidate.ProviderId, candidate.Type, WebServerManagementMode.Integrated,
        candidate.ExecutablePath, candidate.ConfigurationPath, candidate.Version, candidate.DetectedAt,
        new WebServerCapabilities(true, true, true));

    // The recovery owner calls this without loading execution code or dispatching any mutation.
    internal static async Task<bool> ConfirmIntegrationAsync(WebServerMetadataRepository metadata,
        NginxIntegrationCheckpoint checkpoint, string instanceId, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || checkpoint.Instance is not { ConfigurationPath: { } configurationPath } instance
            || instance.Id != instanceId || instance.Id != InstanceId(instance.ExecutablePath)
            || instance.ManagementMode != WebServerManagementMode.Integrated || instance.ProviderId != ProviderKey)
            return false;
        await IntegrationGate.WaitAsync(ct);
        try
        {
            var directory = FindOwnedIncludeDirectory(configurationPath);
            if (directory != LinuxSystemIncludeDirectory || IsSymbolicLink(directory)) return false;
            var anchor = Path.Combine(directory, OwnedFileName);
            if (!File.Exists(anchor) || IsSymbolicLink(anchor)
                || !await MatchesSiteConfigAsync(anchor, AnchorContent(Path.Combine(directory, "relaxkonos.d")), ct)
                || !await metadata.IsSnapshotCurrentAsync(configurationPath, checkpoint.Snapshot, ct))
                return false;
            await metadata.UpsertInstanceAsync(instance, ct, onlyIfChanged: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        { return false; }
        finally { IntegrationGate.Release(); }
    }

    internal static async Task<bool> CleanIntegrationStageAsync(IPrivilegedNginxOperations native, Guid operationId, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return false;
        await IntegrationGate.WaitAsync(ct);
        try { return await CleanIntegrationStageUnderGateAsync(native, operationId, ct); }
        finally { IntegrationGate.Release(); }
    }

    private static async Task<bool> CleanIntegrationStageUnderGateAsync(IPrivilegedNginxOperations native, Guid operationId, CancellationToken ct)
    {
        try
        {
            if (IsSymbolicLink(LinuxSystemIncludeDirectory)) return false;
            var stage = Path.Combine(LinuxSystemIncludeDirectory, $"relaxkonos.{operationId:N}.conf");
            var attributes = File.GetAttributes(stage);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || !await MatchesSiteConfigAsync(stage, AnchorContent(Path.Combine(LinuxSystemIncludeDirectory, "relaxkonos.d")), ct))
                return false;
            var result = await native.DeleteManagedFileAsync(stage, ct);
            return result.Success || result.ProblemCode == PrivilegedProblemCode.NotFound;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private WebServerMetadataRepository IntegrationMetadata => metadata;
    private IPrivilegedNginxOperations IntegrationFiles => privilegedNginx;

    private sealed class IntegrationPorts(NginxWebServerManager nginx, WebServerDto instance,
        WebServerOperationStore.OperationProgress progress) : INginxIntegrationPorts
    {
        private NginxConfigurationSnapshot? snapshot;
        private string? stage;
        private string? destination;
        private string? content;
        private bool stageAttempted;
        public bool CommitBoundaryRecorded { get; private set; }

        public async Task<NginxIntegrationPlan> PrepareAsync(CancellationToken ct)
        {
            if (instance.ConfigurationPath is null) return new("webserver.configuration_not_found", null);
            snapshot = await nginx.IntegrationMetadata.CreateSnapshotAsync(instance, ct);
            if (snapshot is null) return new("webserver.configuration_not_found", null);
            var include = FindOwnedIncludeDirectory(instance.ConfigurationPath);
            if (!OperatingSystem.IsLinux() || include != LinuxSystemIncludeDirectory)
                return new("webserver.include_context_not_supported", snapshot.Id);
            destination = Path.Combine(include, OwnedFileName);
            if (IsSymbolicLink(include) || Path.GetFullPath(destination) != destination || IsSymbolicLink(destination))
                return new("webserver.unsafe_path", snapshot.Id);
            if (File.Exists(destination))
                return IsOwnedFile(destination) ? new(null, snapshot.Id, AlreadyIntegrated: true)
                    : new("webserver.ownership_conflict", snapshot.Id);

            stage = Path.Combine(include, $"relaxkonos.{progress.OperationId:N}.conf");
            content = AnchorContent(Path.Combine(include, "relaxkonos.d"));
            if (File.Exists(stage) || IsSymbolicLink(stage))
                return new("webserver.ownership_conflict", snapshot.Id);
            return new(null, snapshot.Id);
        }

        public Task<bool> IsSnapshotCurrentAsync(CancellationToken ct) =>
            nginx.IntegrationMetadata.IsSnapshotCurrentAsync(instance.ConfigurationPath!, snapshot!, ct);

        public async Task<string?> StageAsync(CancellationToken ct)
        {
            if (File.Exists(stage!) || IsSymbolicLink(stage!)) return "webserver.ownership_conflict";
            stageAttempted = true;
            var result = await nginx.IntegrationFiles.WriteManagedFileAsync(stage!, Encoding.UTF8.GetBytes(content!), ct);
            return result.Success ? null : ToNginxConfigurationProblem(result);
        }

        public async Task<bool> TestAsync(CancellationToken ct) => (await nginx.RunSystemPackageConfigurationTestAsync(ct)).Success;

        public Task CheckpointAsync(CancellationToken ct) =>
            progress.CheckpointIntegrationAsync(new(instance, snapshot!), ct);

        public async Task RecordCommitBoundaryAsync(CancellationToken ct)
        {
            await progress.ReportAsync(WebServerOperationStore.NativeReloadDispatchStage, ct);
            CommitBoundaryRecorded = true;
        }

        public async Task<bool> CommitAsync() =>
            (await nginx.IntegrationFiles.MoveManagedFileAsync(stage!, destination!, overwrite: false, cancellationToken: CancellationToken.None)).Success;

        public async Task DispatchReloadAsync() => _ = await nginx.RunTrackedReloadAsync(progress.OperationId, CancellationToken.None);

        public async Task CleanStageAsync()
        {
            if (stageAttempted && !await CleanIntegrationStageUnderGateAsync(nginx.IntegrationFiles, progress.OperationId, CancellationToken.None))
                throw new NginxIntegrationCleanupException();
        }
    }
}

internal sealed record NginxIntegrationCheckpoint(WebServerDto Instance, NginxConfigurationSnapshot Snapshot);

internal sealed class NginxIntegrationCleanupException()
    : InvalidOperationException("Nginx integration anchor cleanup could not be confirmed.");
