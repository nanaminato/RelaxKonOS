using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Server.WebServer;

internal sealed partial class NginxWebServerManager
{
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

        internal Task<WebServerOperationResult> EnableAcmeHttp01Async(WebServerDto instance, CancellationToken ct) =>
            nginx.EnableAcmeHttp01CoreAsync(instance, ct);

        internal Task<WebServerOperationResult> ApplyManagedLifecycleAsync(ManagedLayout layout, WebServerLifecycleAction action, CancellationToken ct) =>
            nginx.ApplyManagedLifecycleCoreAsync(layout, action, ct);
    }
}
