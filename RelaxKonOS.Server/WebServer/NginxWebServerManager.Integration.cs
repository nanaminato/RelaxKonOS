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

    private async Task<WebServerOperationResult> IntegrateCoreAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        if (instance.ConfigurationPath is null)
        {
            logger.LogWarning("Nginx integration cannot continue because no configuration path was detected. InstanceId={InstanceId}, Executable={Executable}", instance.Id, instance.ExecutablePath);
            return new WebServerOperationResult("webserver.configuration_not_found");
        }
        logger.LogInformation("Starting Nginx integration. InstanceId={InstanceId}, Executable={Executable}, Configuration={Configuration}", instance.Id, instance.ExecutablePath, instance.ConfigurationPath);
        var snapshot = await metadata.CreateSnapshotAsync(instance, cancellationToken);
        if (snapshot is null)
        {
            logger.LogWarning("Nginx integration cannot snapshot the configuration. InstanceId={InstanceId}, Configuration={Configuration}", instance.Id, instance.ConfigurationPath);
            return new WebServerOperationResult("webserver.configuration_not_found");
        }
        var includeDirectory = FindOwnedIncludeDirectory(instance.ConfigurationPath);
        if (includeDirectory is null)
        {
            logger.LogWarning("Nginx integration cannot find a supported include directory. InstanceId={InstanceId}, Configuration={Configuration}, SnapshotId={SnapshotId}", instance.Id, instance.ConfigurationPath, snapshot.Id);
            return new WebServerOperationResult("webserver.include_context_not_supported", snapshot.Id);
        }
        if (Path.GetFileName(includeDirectory) is not "conf.d")
        {
            logger.LogWarning("Nginx integration only supports a conf.d include directory. InstanceId={InstanceId}, IncludeDirectory={IncludeDirectory}, SnapshotId={SnapshotId}", instance.Id, includeDirectory, snapshot.Id);
            return new WebServerOperationResult("webserver.include_context_not_supported", snapshot.Id);
        }
        if (OperatingSystem.IsLinux() && !string.Equals(includeDirectory, LinuxSystemIncludeDirectory, StringComparison.Ordinal))
        {
            logger.LogWarning("Nginx integration only supports the Linux system include directory handled by the privileged helper. InstanceId={InstanceId}, IncludeDirectory={IncludeDirectory}, ExpectedIncludeDirectory={ExpectedIncludeDirectory}, SnapshotId={SnapshotId}", instance.Id, includeDirectory, LinuxSystemIncludeDirectory, snapshot.Id);
            return new WebServerOperationResult("webserver.include_context_not_supported", snapshot.Id);
        }
        if (IsSymbolicLink(includeDirectory))
        {
            logger.LogWarning("Nginx integration rejected a symbolic-link include directory. InstanceId={InstanceId}, IncludeDirectory={IncludeDirectory}, SnapshotId={SnapshotId}", instance.Id, includeDirectory, snapshot.Id);
            return new WebServerOperationResult("webserver.unsafe_path", snapshot.Id);
        }

        var destination = Path.Combine(includeDirectory, OwnedFileName);
        if (Path.GetFullPath(destination) != destination || IsSymbolicLink(destination))
        {
            logger.LogWarning("Nginx integration rejected an unsafe destination. InstanceId={InstanceId}, Destination={Destination}, SnapshotId={SnapshotId}", instance.Id, destination, snapshot.Id);
            return new WebServerOperationResult("webserver.unsafe_path", snapshot.Id);
        }
        if (File.Exists(destination))
        {
            var problem = IsOwnedFile(destination) ? "" : "webserver.ownership_conflict";
            logger.LogInformation("Nginx integration found an existing anchor. InstanceId={InstanceId}, Destination={Destination}, IsRelaxKonOSOwned={IsRelaxKonOSOwned}, SnapshotId={SnapshotId}", instance.Id, destination, string.IsNullOrEmpty(problem), snapshot.Id);
            return new WebServerOperationResult(problem, snapshot.Id);
        }

        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the per-provider transaction lock so an external change cannot be overwritten.
            if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
            {
                logger.LogWarning("Nginx integration stopped because the configuration changed before staging. InstanceId={InstanceId}, Configuration={Configuration}, SnapshotId={SnapshotId}", instance.Id, instance.ConfigurationPath, snapshot.Id);
                return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
            }
            if (File.Exists(destination))
            {
                var problem = IsOwnedFile(destination) ? "" : "webserver.ownership_conflict";
                logger.LogWarning("Nginx integration stopped because its anchor appeared during staging. InstanceId={InstanceId}, Destination={Destination}, IsRelaxKonOSOwned={IsRelaxKonOSOwned}, SnapshotId={SnapshotId}", instance.Id, destination, string.IsNullOrEmpty(problem), snapshot.Id);
                return new WebServerOperationResult(problem, snapshot.Id);
            }
            // Keep the staged file in the include graph (and on the same filesystem), so
            // nginx -t validates the exact file that will be atomically renamed into place.
            var stage = Path.Combine(includeDirectory, $"relaxkonos.{Guid.NewGuid():N}.conf");
            var committed = false;
            var stageWritten = false;
            try
            {
                var staged = await privilegedNginx.WriteManagedFileAsync(stage, Encoding.UTF8.GetBytes(AnchorContent(Path.Combine(includeDirectory, "relaxkonos.d"))), cancellationToken);
                if (!staged.Success)
                {
                    logger.LogWarning("Nginx integration could not stage its anchor through the privileged helper. InstanceId={InstanceId}, Stage={Stage}, ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}, SnapshotId={SnapshotId}", instance.Id, stage, staged.ProblemCode, staged.ExitCode, staged.Error, snapshot.Id);
                    return new WebServerOperationResult(ToNginxConfigurationProblem(staged), snapshot.Id);
                }
                stageWritten = true;
                logger.LogInformation("Nginx integration anchor staged. InstanceId={InstanceId}, Stage={Stage}, Destination={Destination}, SnapshotId={SnapshotId}", instance.Id, stage, destination, snapshot.Id);
                if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
                {
                    logger.LogWarning("Nginx integration stopped because the configuration changed after staging. InstanceId={InstanceId}, Configuration={Configuration}, SnapshotId={SnapshotId}", instance.Id, instance.ConfigurationPath, snapshot.Id);
                    return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
                }
                var test = await RunSystemPackageConfigurationTestAsync(cancellationToken);
                if (!test.Success)
                {
                    logger.LogWarning("Nginx integration configuration validation failed. InstanceId={InstanceId}, Executable={Executable}, Stage={Stage}, Output={Output}, SnapshotId={SnapshotId}", instance.Id, instance.ExecutablePath, stage, CommandOutputForLog(test.Output), snapshot.Id);
                    return new WebServerOperationResult("webserver.config_test_failed", snapshot.Id);
                }
                if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
                {
                    logger.LogWarning("Nginx integration stopped because the configuration changed after validation. InstanceId={InstanceId}, Configuration={Configuration}, SnapshotId={SnapshotId}", instance.Id, instance.ConfigurationPath, snapshot.Id);
                    return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
                }
                var moved = await privilegedNginx.MoveManagedFileAsync(stage, destination, overwrite: false, cancellationToken: cancellationToken);
                if (!moved.Success)
                {
                    logger.LogWarning("Nginx integration could not commit its anchor through the privileged helper. InstanceId={InstanceId}, Stage={Stage}, Destination={Destination}, ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}, SnapshotId={SnapshotId}", instance.Id, stage, destination, moved.ProblemCode, moved.ExitCode, moved.Error, snapshot.Id);
                    return new WebServerOperationResult(ToNginxConfigurationProblem(moved), snapshot.Id);
                }
                stageWritten = false;
                committed = true;
                logger.LogInformation("Nginx integration anchor committed. InstanceId={InstanceId}, Destination={Destination}, SnapshotId={SnapshotId}", instance.Id, destination, snapshot.Id);
                var reload = await RunSystemdNginxOperationAsync("reload", cancellationToken);
                if (reload.Success) return new WebServerOperationResult("", snapshot.Id);

                // The old workers normally keep the prior configuration. Restore disk state and attempt a rollback reload.
                logger.LogWarning("Nginx integration reload failed; restoring the prior configuration. InstanceId={InstanceId}, Executable={Executable}, Destination={Destination}, ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}, SnapshotId={SnapshotId}", instance.Id, instance.ExecutablePath, destination, reload.ProblemCode, reload.ExitCode, reload.Error, snapshot.Id);
                var deleted = await privilegedNginx.DeleteManagedFileAsync(destination, cancellationToken);
                if (!deleted.Success)
                {
                    logger.LogError("Nginx integration could not remove its anchor during rollback through the privileged helper. InstanceId={InstanceId}, Destination={Destination}, ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}, SnapshotId={SnapshotId}", instance.Id, destination, deleted.ProblemCode, deleted.ExitCode, deleted.Error, snapshot.Id);
                    return new WebServerOperationResult(ToNginxConfigurationProblem(deleted), snapshot.Id);
                }
                committed = false;
                _ = await RunSystemPackageConfigurationTestAsync(cancellationToken);
                _ = await RunSystemdNginxOperationAsync("reload", cancellationToken);
                return new WebServerOperationResult("webserver.reload_failed", snapshot.Id);
            }
            catch (Exception exception) when (exception is not UnauthorizedAccessException and not IOException and not OperationCanceledException)
            {
                // Cancellation and unexpected process errors must not leave an unverified disk config behind.
                logger.LogError(exception, "Nginx integration encountered an unexpected error. InstanceId={InstanceId}, Executable={Executable}, Configuration={Configuration}, Stage={Stage}, Destination={Destination}, Committed={Committed}, SnapshotId={SnapshotId}", instance.Id, instance.ExecutablePath, instance.ConfigurationPath, stage, destination, committed, snapshot.Id);
                if (committed)
                {
                    var rollback = await privilegedNginx.DeleteManagedFileAsync(destination, CancellationToken.None);
                    if (!rollback.Success)
                        logger.LogError("Nginx integration could not remove its anchor after an unexpected error. InstanceId={InstanceId}, Destination={Destination}, ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}, SnapshotId={SnapshotId}", instance.Id, destination, rollback.ProblemCode, rollback.ExitCode, rollback.Error, snapshot.Id);
                }
                throw;
            }
            finally
            {
                if (stageWritten)
                {
                    var cleanup = await privilegedNginx.DeleteManagedFileAsync(stage, CancellationToken.None);
                    if (!cleanup.Success && cleanup.ProblemCode != PrivilegedProblemCode.NotFound)
                        logger.LogWarning("Nginx integration could not remove its staged anchor through the privileged helper. InstanceId={InstanceId}, Stage={Stage}, ProblemCode={ProblemCode}, ExitCode={ExitCode}, Error={Error}, SnapshotId={SnapshotId}", instance.Id, stage, cleanup.ProblemCode, cleanup.ExitCode, cleanup.Error, snapshot.Id);
                }
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogError(exception, "Nginx integration was denied access to host configuration. InstanceId={InstanceId}, Configuration={Configuration}, Destination={Destination}, SnapshotId={SnapshotId}", instance.Id, instance.ConfigurationPath, destination, snapshot.Id);
            return new WebServerOperationResult("webserver.config_elevation_required", snapshot.Id);
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Nginx integration encountered an I/O conflict. InstanceId={InstanceId}, Configuration={Configuration}, Destination={Destination}, SnapshotId={SnapshotId}", instance.Id, instance.ConfigurationPath, destination, snapshot.Id);
            return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
        }
        finally { IntegrationGate.Release(); }
    }

    private async Task<WebServerOperationResult> EnableAcmeHttp01CoreAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        var directory = GetSitesDirectory(instance);
        if (directory is null) return new WebServerOperationResult("webserver.acme_integration_required");
        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            var anchorProblem = await EnsureSiteIncludeAnchorAsync(instance, cancellationToken);
            if (anchorProblem is not null) return new WebServerOperationResult("webserver.acme_integration_required");
            if (IsSymbolicLink(directory)) return new WebServerOperationResult("webserver.unsafe_path");
            if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            var sites = await ReadSitesAsync(instance, cancellationToken);
            if (sites.Count == 0) return new WebServerOperationResult("webserver.acme_no_managed_sites");

            var marker = Path.Combine(directory, AcmeEnabledFileName);
            if (File.Exists(marker) && !IsAcmeHttp01Enabled(directory)) return new WebServerOperationResult("webserver.ownership_conflict");
            if (!File.Exists(marker))
            {
                var stage = marker + ".stage";
                try
                {
                    if (!await WriteNginxFileAsync(stage, OwnershipMarker + "\nACME HTTP-01 enabled.\n", cancellationToken)
                        || !await MoveNginxFileAsync(stage, marker, false, cancellationToken)) return new("webserver.config_elevation_required");
                }
                finally { if (File.Exists(stage)) await DeleteNginxFileAsync(stage, CancellationToken.None); }
            }

            foreach (var site in sites)
            {
                var problem = await WriteSiteConfigurationAsync(instance, site, cancellationToken);
                if (problem is not null)
                {
                    logger.LogWarning("Nginx ACME HTTP-01 enablement could not update site. InstanceId={InstanceId} SiteId={SiteId} Problem={Problem}", instance.Id, site.Id, problem);
                    return new WebServerOperationResult(problem);
                }
            }
            logger.LogInformation("Nginx ACME HTTP-01 routing enabled. InstanceId={InstanceId} Sites={SiteCount} ChallengeRoot={ChallengeRoot}", instance.Id, sites.Count, webRootChallenges.RootPath);
            return new WebServerOperationResult("");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.config_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.configuration_changed"); }
        finally { IntegrationGate.Release(); }
    }

}
