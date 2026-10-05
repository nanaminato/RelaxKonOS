using System.Security.Claims;
using System.Text.Json;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.BackupRecovery;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.BackupRecovery;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Encrypted definition backup, read-only preflight, and explicitly confirmed new-instance restore endpoints.</summary>
public static class BackupRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapBackupRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(BackupRecoveryApiRoutes.Root)
            .RequireAuthorization()
            .WithTags("BackupRecovery")
            .RequireHostFeature(ServerHostFeature.ApplicationDeployments);
        group.MapGet(BackupRecoveryApiRoutes.AvailabilityPattern, (IBackupRecoveryKeyProvider keys) => Results.Ok(keys.Availability))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ReadPolicy);
        group.MapPost(BackupRecoveryApiRoutes.CreateDefinitionPattern, async (Guid applicationId, HttpContext http,
            ApplicationDefinitionBackupService backups, CancellationToken ct) => await HandleAsync(async () =>
            {
                var key = http.Request.Headers["Idempotency-Key"].ToString();
                if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
                    return Problem("backup-recovery.idempotency_key_required", 400);
                var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub")
                    ?? throw new UnauthorizedAccessException();
                var manifest = await backups.CreateAsync(applicationId, actor, key, ct);
                return Results.Accepted(BackupRecoveryApiRoutes.Backup(manifest.BackupId), manifest);
            }))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        group.MapGet(BackupRecoveryApiRoutes.DefinitionBackupRequestPattern, (Guid applicationId, HttpContext http,
            ApplicationDefinitionBackupService backups) => HandleAsync(() =>
            {
                var key = http.Request.Headers["Idempotency-Key"].ToString();
                if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
                    return Task.FromResult(Problem("backup-recovery.idempotency_key_required", 400));
                var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub")
                    ?? throw new UnauthorizedAccessException();
                return Task.FromResult(backups.Find(applicationId, actor, key) is { } manifest
                    ? Results.Ok(manifest) : Results.NotFound());
            }))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        group.MapGet(BackupRecoveryApiRoutes.ApplicationBackupsPattern, (Guid applicationId, ApplicationDefinitionBackupService backups) =>
            Results.Ok(backups.List(applicationId))).RequireAuthorization(ApplicationDeploymentEndpoints.ReadPolicy);
        group.MapGet(BackupRecoveryApiRoutes.BackupPattern, (Guid backupId, ApplicationDefinitionBackupService backups) =>
            backups.Get(backupId) is { } manifest ? Results.Ok(manifest) : Results.NotFound())
            .RequireAuthorization(ApplicationDeploymentEndpoints.ReadPolicy);
        group.MapGet(BackupRecoveryApiRoutes.PreflightPattern, async (Guid backupId, ApplicationDefinitionBackupService backups, CancellationToken ct) =>
            await HandleAsync(async () => Results.Ok(await backups.PreflightAsync(backupId, ct))))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ReadPolicy);
        group.MapPost(BackupRecoveryApiRoutes.RestoreDefinitionPattern, async (Guid backupId, RestoreApplicationDefinitionRequest request,
            HttpContext http, ApplicationDefinitionBackupService backups, ApplicationDeploymentDefinitionMutationStore mutations, ApplicationDeploymentManager manager, CancellationToken ct) =>
            await HandleAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"]))
                    return Problem("backup-recovery.idempotency_key_required", 400);
                if (!request.Confirmed) return Problem(BackupRecoveryProblemCodes.RestoreConfirmationRequired, 400);
                var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub")
                    ?? throw new UnauthorizedAccessException();
                var restored = await mutations.ExecuteAsync(actor, http.Request.Headers["Idempotency-Key"].ToString(), "restore",
                    ApplicationDeploymentValidation.Reference(JsonSerializer.Serialize((backupId, request))),
                    () => backups.RestoreDefinitionAsync(backupId, request, actor, ct), manager.ReadReceipt);
                return Results.Created(ApplicationDeploymentApiRoutes.Application(restored.Id), restored);
            }))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        return app;
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (BackupRecoveryException error) { return Problem(error.ProblemCode,
            error.ProblemCode is BackupRecoveryProblemCodes.BackupNotFound ? 404 : 409); }
        catch (ApplicationDeploymentException error) { return Problem(error.ProblemCode, error.StatusCode); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }

    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code,
        type: "https://relaxkonos.app/problems/" + code,
        extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
