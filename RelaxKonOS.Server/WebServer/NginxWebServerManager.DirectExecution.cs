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

        internal async Task<WebServerOperationResult> ReloadAsync(WebServerDto instance, CancellationToken ct)
        {
            if (nginx.UsesSystemPackageNginx(instance))
            {
                var helper = await nginx.RunSystemdNginxOperationAsync("reload", ct);
                return new(helper.Success ? "" : ToWebServerProblem(helper.ProblemCode, "webserver.reload_failed"));
            }
            var reload = await nginx.RunNginxAsync(instance.ExecutablePath,
                instance.ManagementMode == WebServerManagementMode.Managed
                    ? nginx.ManagedArguments(nginx.GetManagedLayout(), ["-s", "reload"])
                    : ["-s", "reload"], ct);
            return new(reload.Success ? "" : "webserver.reload_failed");
        }

        internal Task<WebServerOperationResult> EnableAcmeHttp01Async(WebServerDto instance, CancellationToken ct) =>
            nginx.EnableAcmeHttp01CoreAsync(instance, ct);

        internal Task<WebServerOperationResult> ApplyManagedLifecycleAsync(ManagedLayout layout, WebServerLifecycleAction action, CancellationToken ct) =>
            nginx.ApplyManagedLifecycleCoreAsync(layout, action, ct);
    }
}
