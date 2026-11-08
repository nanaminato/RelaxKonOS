using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.WebServer;

internal sealed partial class NginxWebServerManager
{
    private string AcmeChallengeRoot => webRootChallenges.RootPath;

    /// <summary>Executes an admitted direct operation through the original Core Nginx provider.</summary>
    internal sealed class DirectExecution(NginxWebServerManager nginx, WebServerMetadataRepository metadata, ILogger<DirectExecution> logger)
    {
        internal async Task<WebServerOperationResult> IntegrateAsync(WebServerDto instance,
            WebServerOperationStore.OperationProgress progress, CancellationToken ct)
        {
            await IntegrationGate.WaitAsync(ct);
            try
            {
                var ports = new IntegrationPorts(nginx, instance, progress);
                var lease = WebServerExecutionCode.Shared.Acquire<INginxIntegrationExecutor>(
                    "RelaxKonOS.Server.WebServer.NginxIntegrationExecutor", ports);
                progress.HoldExecution(lease);
                var result = await RunIntegrationAsync(lease.Executor, ports, ct);
                if (result.Problem.Length == 0) await metadata.UpsertInstanceAsync(instance, ct, onlyIfChanged: true);
                return new(result.Problem, result.SnapshotId);
            }
            finally { IntegrationGate.Release(); }
        }

        private static async Task<NginxIntegrationResult> RunIntegrationAsync(INginxIntegrationExecutor executor,
            IntegrationPorts ports, CancellationToken ct)
        {
            try { return await executor.IntegrateAsync(ct); }
            catch (NginxIntegrationCleanupException) { throw new NginxIntegrationCleanupException(); }
            catch (ResidentWorkCompletionException)
            { throw new ResidentWorkCompletionException(new InvalidOperationException("Nginx integration checkpoint could not be saved.")); }
            catch (Exception) when (ports.CommitBoundaryRecorded) { return new(NginxReloadOutcome.RecoveryRequired, null); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (IOException) { return new("webserver.configuration_changed", null); }
            catch (UnauthorizedAccessException) { return new("webserver.config_elevation_required", null); }
            catch (Exception) { throw new InvalidOperationException("Nginx integration execution failed."); }
        }

        internal async Task<WebServerOperationResult> ReloadAsync(WebServerDto instance, WebServerOperationStore.OperationProgress progress, CancellationToken ct)
        {
            await IntegrationGate.WaitAsync(ct);
            try
            {
                return await ReloadUnderGateAsync(instance, progress, ct);
            }
            finally { IntegrationGate.Release(); }
        }

        internal Task<WebServerOperationResult> ReloadRenewedCertificateAsync(PendingNginxRenewal renewal, string instanceId,
            CertificateOperationStore certificateOperations, WebServerOperationStore.OperationProgress progress, CancellationToken ct) =>
            certificateOperations.UnderCertificateGateAsync(renewal.CertificateId, async token =>
            {
                await IntegrationGate.WaitAsync(token);
                try
                {
                    var instance = (await nginx.DiscoverAsync(token)).FirstOrDefault(item => item.Id == instanceId);
                    var problem = await nginx.ValidateCertificateReloadAsync(renewal, instance, token);
                    return problem is null ? await ReloadUnderGateAsync(instance!, progress, token) : new(problem);
                }
                finally { IntegrationGate.Release(); }
            }, ct);

        private async Task<WebServerOperationResult> ReloadUnderGateAsync(WebServerDto instance,
            WebServerOperationStore.OperationProgress progress, CancellationToken ct)
        {
            var lease = WebServerExecutionCode.Shared.Acquire<INginxReloadExecutor>(
                "RelaxKonOS.Server.WebServer.NginxReloadExecutor", new ReloadPorts(nginx, instance, progress));
            progress.HoldExecution(lease);
            return new(await RunReloadAsync(lease.Executor, ct));
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
                    "RelaxKonOS.Server.WebServer.NginxWebRootExecutor", new WebRootPorts(nginx, instance, directory, progress, logger));
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
            catch (ResidentWorkCompletionException)
            { throw new ResidentWorkCompletionException(new InvalidOperationException("WebRoot checkpoint could not be saved.")); }
            catch (NginxProcessCleanupException)
            {
                throw new NginxProcessCleanupException(new InvalidOperationException("WebRoot command did not drain."));
            }
            catch (Exception) { throw new InvalidOperationException("Nginx WebRoot execution failed."); }
        }

        private sealed class WebRootPorts(NginxWebServerManager nginx, WebServerDto instance, string directory,
            WebServerOperationStore.OperationProgress progress, ILogger logger)
            : INginxWebRootPorts
        {
            private readonly string marker = Path.Combine(directory, AcmeEnabledFileName);
            private readonly bool tracked = nginx.UsesSystemPackageNginx(instance);

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
                if (tracked && sites.Count != 0)
                    await progress.PlanWebRootAsync(instance, sites.Select(site => site.Id).ToArray(), ct);
                return new(null, sites);
            }

            public WebRootMarkerState ReadMarker() => IsAcmeHttp01Enabled(directory) ? WebRootMarkerState.Owned
                : File.Exists(marker) || IsSymbolicLink(marker) ? WebRootMarkerState.Conflict : WebRootMarkerState.Missing;

            public Task<bool> StageMarkerAsync(CancellationToken ct) =>
                nginx.WriteNginxFileAsync(marker + ".stage", AcmeEnabledMarkerContent, ct);

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
                var problem = tracked ? await nginx.ApplyTrackedWebRootSiteAsync(instance, site, progress, ct)
                    : await nginx.WriteSiteConfigurationAsync(instance, site, ct, preserveUnverifiedReload: true);
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

        internal async Task<WebServerOperationResult> ApplyTrackedLifecycleAsync(ManagedLayout layout, NginxSystemServiceAction action,
            string kind, WebServerOperationStore.OperationProgress progress, CancellationToken ct)
        {
            await IntegrationGate.WaitAsync(ct);
            try
            {
                var ports = new LifecyclePorts(nginx, layout, action, progress);
                var lease = WebServerExecutionCode.Shared.Acquire<INginxLifecycleExecutor>(
                    "RelaxKonOS.Server.WebServer.NginxLifecycleExecutor", ports);
                progress.HoldExecution(lease);
                var succeeded = await RunLifecycleAsync(lease.Executor, ports, ct);
                return succeeded is null ? new(NginxReloadOutcome.RecoveryRequired)
                    : new(succeeded.Value ? "" : $"webserver.{kind}_failed", NativeCompletionConfirmed: true);
            }
            finally { IntegrationGate.Release(); }
        }

        private static async Task<bool?> RunLifecycleAsync(INginxLifecycleExecutor executor, LifecyclePorts ports, CancellationToken ct)
        {
            try { return await executor.RunAsync(ct); }
            catch (ResidentWorkCompletionException)
            { throw new ResidentWorkCompletionException(new InvalidOperationException("Nginx lifecycle checkpoint could not be saved.")); }
            catch (NginxProcessCleanupException)
            { throw new NginxProcessCleanupException(new InvalidOperationException("Nginx lifecycle command did not drain.")); }
            catch (Exception) when (ports.DispatchRecorded) { return null; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (Exception) { throw new InvalidOperationException("Nginx lifecycle execution failed."); }
        }

        private sealed class LifecyclePorts(NginxWebServerManager nginx, ManagedLayout layout, NginxSystemServiceAction action,
            WebServerOperationStore.OperationProgress progress) : INginxLifecyclePorts
        {
            public Guid OperationId => progress.OperationId;
            public bool DispatchRecorded { get; private set; }

            public async Task ReportDispatchAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                await progress.ReportAsync(WebServerOperationStore.NativeLifecycleDispatchStage, ct);
                DispatchRecorded = true;
            }

            public Task ReportConfirmedAsync() =>
                progress.ReportAsync(WebServerOperationStore.NativeLifecycleConfirmedStage, CancellationToken.None);

            public async Task<PrivilegedOperationResult> RunTrackedAsync()
            {
                await nginx.StopLegacyCustomManagedInstanceAsync(layout, CancellationToken.None);
                return await nginx.RunTrackedLifecycleAsync(OperationId, action);
            }
        }
    }

    private Task<PrivilegedOperationResult> RunTrackedLifecycleAsync(Guid id, NginxSystemServiceAction action) =>
        privilegedNginx.RunTrackedLifecycleAsync(id, action, CancellationToken.None);
}

internal sealed class NginxWebRootCleanupException()
    : InvalidOperationException("Nginx WebRoot file cleanup could not be confirmed.");
