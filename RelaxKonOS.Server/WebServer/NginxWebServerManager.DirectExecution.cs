using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Server.WebServer;

internal sealed partial class NginxWebServerManager
{
    private string AcmeChallengeRoot => webRootChallenges.RootPath;

    /// <summary>Executes an admitted direct operation through the original Core Nginx provider.</summary>
    internal sealed class DirectExecution(NginxWebServerManager nginx, WebServerMetadataRepository metadata, ILogger<DirectExecution> logger)
    {
        internal async Task<WebServerOperationResult> IntegrateAsync(WebServerDto instance, CancellationToken ct)
        {
            var result = await nginx.IntegrateCoreAsync(instance, ct);
            if (string.IsNullOrEmpty(result.ProblemCode))
            {
                await metadata.UpsertInstanceAsync(instance, ct);
                logger.LogInformation("Nginx integration completed. InstanceId={InstanceId}, Executable={Executable}, Configuration={Configuration}",
                    instance.Id, instance.ExecutablePath, instance.ConfigurationPath);
            }
            else
                logger.LogWarning("Nginx integration failed. InstanceId={InstanceId}, Problem={Problem}, SnapshotId={SnapshotId}",
                    instance.Id, result.ProblemCode, result.SnapshotId);
            return result;
        }

        internal async Task<WebServerOperationResult> ReloadAsync(WebServerDto instance, WebServerOperationStore.OperationProgress progress, CancellationToken ct)
        {
            await IntegrationGate.WaitAsync(ct);
            try
            {
                var lease = WebServerExecutionCode.Shared.Acquire<INginxReloadExecutor>(
                    "RelaxKonOS.Server.WebServer.NginxReloadExecutor", new ReloadPorts(nginx, instance, progress));
                progress.HoldExecution(lease);
                return new(await RunReloadAsync(lease.Executor, ct));
            }
            finally { IntegrationGate.Release(); }
        }

        private static async Task<string> RunReloadAsync(INginxReloadExecutor executor, CancellationToken ct)
        {
            try { return await executor.ReloadAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (NginxProcessCleanupException)
            {
                throw new NginxProcessCleanupException(new InvalidOperationException("Direct reload did not drain."));
            }
            catch (Exception) { throw new InvalidOperationException("Nginx reload execution failed."); }
        }

        private sealed class ReloadPorts(NginxWebServerManager nginx, WebServerDto instance, WebServerOperationStore.OperationProgress progress) : INginxReloadPorts
        {
            public Guid OperationId => progress.OperationId;
            public bool UsesSystemPackage => nginx.UsesSystemPackageNginx(instance);

            public Task ReportDispatchAsync(CancellationToken cancellationToken) =>
                progress.ReportAsync(WebServerOperationStore.NativeReloadDispatchStage, cancellationToken);

            public Task ReportResponseAsync(bool confirmed) => progress.ReportAsync(
                confirmed ? WebServerOperationStore.NativeReloadConfirmedStage : "running", CancellationToken.None);

            public Task<PrivilegedOperationResult> RunTrackedAsync(Guid operationId, CancellationToken cancellationToken) =>
                nginx.RunTrackedReloadAsync(operationId, cancellationToken);

            public async Task<bool> RunDirectAsync(CancellationToken cancellationToken) => (await nginx.RunNginxAsync(instance.ExecutablePath,
                instance.ManagementMode == WebServerManagementMode.Managed
                    ? nginx.ManagedArguments(nginx.GetManagedLayout(), ["-s", "reload"])
                    : ["-s", "reload"], cancellationToken)).Success;

            public string ToReloadProblem(PrivilegedProblemCode problem) => ToWebServerProblem(problem, "webserver.reload_failed");
        }

        internal async Task<WebServerOperationResult> EnableAcmeHttp01Async(WebServerDto instance,
            WebServerOperationStore.OperationProgress progress, CancellationToken ct)
        {
            if (GetSitesDirectory(instance) is not { } directory) return new("webserver.acme_integration_required");
            await IntegrationGate.WaitAsync(ct);
            try
            {
                var lease = WebServerExecutionCode.Shared.Acquire<INginxWebRootExecutor>(
                    "RelaxKonOS.Server.WebServer.NginxWebRootExecutor", new WebRootPorts(nginx, instance, directory, logger));
                progress.HoldExecution(lease);
                return new(await RunWebRootAsync(lease.Executor, ct));
            }
            finally { IntegrationGate.Release(); }
        }

        private static async Task<string> RunWebRootAsync(INginxWebRootExecutor executor, CancellationToken ct)
        {
            try { return await executor.EnableAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (NginxWebRootCleanupException) { throw new NginxWebRootCleanupException(); }
            catch (NginxProcessCleanupException)
            {
                throw new NginxProcessCleanupException(new InvalidOperationException("WebRoot command did not drain."));
            }
            catch (Exception) { throw new InvalidOperationException("Nginx WebRoot execution failed."); }
        }

        private sealed class WebRootPorts(NginxWebServerManager nginx, WebServerDto instance, string directory, ILogger logger)
            : INginxWebRootPorts
        {
            private readonly string marker = Path.Combine(directory, AcmeEnabledFileName);

            public async Task<WebRootSitePlan> PrepareAsync(CancellationToken ct)
            {
                var anchorProblem = await nginx.EnsureSiteIncludeAnchorAsync(instance, ct);
                if (anchorProblem is not null) return new("webserver.acme_integration_required", []);
                if (IsSymbolicLink(directory)) return new("webserver.unsafe_path", []);
                if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                if (File.Exists(marker + ".stage") || Directory.Exists(marker + ".stage"))
                    return new("webserver.ownership_conflict", []);
                var sites = await nginx.ReadSitesAsync(instance, ct);
                // A new enablement must not reload earlier sites before discovering an unresolved backup.
                foreach (var site in sites)
                {
                    var config = Path.Combine(directory, site.Id + ".conf");
                    if (!IsRelaxKonOSSiteConfig(config) || File.Exists(config + ".rollback"))
                        return new("webserver.site_save_failed", []);
                }
                return new(null, sites);
            }

            public WebRootMarkerState ReadMarker() => IsAcmeHttp01Enabled(directory) ? WebRootMarkerState.Owned
                : File.Exists(marker) || IsSymbolicLink(marker) ? WebRootMarkerState.Conflict : WebRootMarkerState.Missing;

            public Task<bool> StageMarkerAsync(CancellationToken ct) =>
                nginx.WriteNginxFileAsync(marker + ".stage", OwnershipMarker + "\nACME HTTP-01 enabled.\n", ct);

            public Task<bool> PublishMarkerAsync(CancellationToken ct) =>
                nginx.MoveNginxFileAsync(marker + ".stage", marker, overwrite: false, ct);

            public async Task CleanMarkerStageAsync()
            {
                var stage = marker + ".stage";
                if (File.Exists(stage) && !await nginx.DeleteNginxFileAsync(stage, CancellationToken.None))
                    throw new NginxWebRootCleanupException();
            }

            public async Task<string?> ApplySiteAsync(WebServerSiteDto site, CancellationToken ct)
            {
                var problem = await nginx.WriteSiteConfigurationAsync(instance, site, ct, preserveUnverifiedReload: true);
                if (problem is not null)
                    logger.LogWarning("Nginx ACME HTTP-01 enablement could not update site. InstanceId={InstanceId} SiteId={SiteId} Problem={Problem}",
                        instance.Id, site.Id, problem);
                return problem;
            }

            public void ReportEnabled(int siteCount) =>
                logger.LogInformation("Nginx ACME HTTP-01 routing enabled. InstanceId={InstanceId} Sites={SiteCount} ChallengeRoot={ChallengeRoot}",
                    instance.Id, siteCount, nginx.AcmeChallengeRoot);
        }

        internal Task<WebServerOperationResult> ApplyManagedLifecycleAsync(ManagedLayout layout, WebServerLifecycleAction action, CancellationToken ct) =>
            nginx.ApplyManagedLifecycleCoreAsync(layout, action, ct);
    }
}

internal sealed class NginxWebRootCleanupException()
    : InvalidOperationException("Nginx WebRoot file cleanup could not be confirmed.");
