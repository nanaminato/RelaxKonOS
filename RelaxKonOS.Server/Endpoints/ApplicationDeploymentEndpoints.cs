using System.Security.Claims;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// The containerised-application deployment surface. Every long action answers <c>202 Accepted</c> with
/// a durable operation, and every mutating action requires an idempotency key, so a retried request can
/// never publish a second revision or start a second container. The observed state reported by a read
/// is reconciled against real Docker resources rather than the ledger alone.
/// </summary>
public static class ApplicationDeploymentEndpoints
{
    public const string ReadPolicy = "ApplicationDeploymentsRead";
    public const string ManagePolicy = "ApplicationDeploymentsManage";

    public static IEndpointRouteBuilder MapApplicationDeploymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApplicationDeploymentApiRoutes.Root)
            .RequireAuthorization()
            .WithTags("ApplicationDeployments")
            .RequireHostFeature(ServerHostFeature.ApplicationDeployments);

        // --- Templates and application records ------------------------------------------------
        group.MapGet(ApplicationDeploymentApiRoutes.TemplatesPattern,
            (ApplicationDeploymentManager manager) => Handle(() => Results.Ok(manager.Templates())))
            .RequireAuthorization(ReadPolicy);

        group.MapGet(ApplicationDeploymentApiRoutes.Applications,
            (ApplicationDeploymentManager manager, CancellationToken ct) =>
                HandleAsync(async () => Results.Ok(await manager.ListAsync(ct))))
            .RequireAuthorization(ReadPolicy);

        group.MapPost(ApplicationDeploymentApiRoutes.CreateApplication,
            (CreateApplicationRequest request, HttpContext http, ApplicationDeploymentManager manager,
                ApplicationDeploymentDefinitionMutationStore mutations, CancellationToken ct) =>
                HandleAsync(async () => Results.Ok(await mutations.ExecuteAsync(Actor(http.User), Key(http), "create",
                    RequestReference(request), () => manager.CreateAsync(request, Actor(http.User), ct)))))
            .RequireAuthorization(ManagePolicy);

        group.MapGet(ApplicationDeploymentApiRoutes.ApplicationPattern,
            (Guid applicationId, ApplicationDeploymentManager manager, CancellationToken ct) =>
                HandleAsync(async () => Results.Ok(await manager.SnapshotAsync(applicationId, ct))))
            .RequireAuthorization(ReadPolicy);

        group.MapPut(ApplicationDeploymentApiRoutes.ApplicationPattern,
            (Guid applicationId, UpdateApplicationRequest request, HttpContext http, ApplicationDeploymentManager manager,
                ApplicationDeploymentDefinitionMutationStore mutations, CancellationToken ct) =>
                HandleAsync(async () => Results.Ok(await mutations.ExecuteAsync(Actor(http.User), Key(http), "update",
                    RequestReference((applicationId, request)), () => manager.UpdateAsync(applicationId, request, ct)))))
            .RequireAuthorization(ManagePolicy);

        group.MapGet(ApplicationDeploymentApiRoutes.RevisionsPattern,
            (Guid applicationId, ApplicationDeploymentManager manager) =>
                Handle(() => Results.Ok(manager.Revisions(applicationId))))
            .RequireAuthorization(ReadPolicy);

        group.MapGet(ApplicationDeploymentApiRoutes.OperationsPattern,
            (Guid applicationId, int? limit, ApplicationDeploymentManager manager) =>
                Handle(() => Results.Ok(manager.Operations(applicationId, limit ?? 50))))
            .RequireAuthorization(ReadPolicy);

        group.MapGet(ApplicationDeploymentApiRoutes.LogsPattern,
            (Guid applicationId, int? tail, ApplicationDeploymentManager manager, CancellationToken ct) =>
                HandleAsync(async () => Results.Ok(await manager.LogsAsync(applicationId, tail ?? 200, ct))))
            .RequireAuthorization(ReadPolicy);

        // --- Long-running operations ---------------------------------------------------------
        group.MapPost(ApplicationDeploymentApiRoutes.DeployPattern,
            (Guid applicationId, DeployApplicationRequest request, HttpContext http,
                ApplicationDeploymentCoordinator coordinator, ApplicationDeploymentManager manager) => Handle(() =>
            {
                if (!request.Confirmed) return Problem(ApplicationDeploymentProblemCodes.ConfirmationRequired, 400);
                _ = manager.Require(applicationId);
                var operation = coordinator.Start(
                    new DeploymentRequest(applicationId, DeploymentOperationKind.Deploy, request.Source),
                    Actor(http.User), Key(http));
                return Accepted(operation);
            }))
            .RequireAuthorization(ManagePolicy);

        group.MapPost(ApplicationDeploymentApiRoutes.RollbackPattern,
            (Guid applicationId, RollbackApplicationRequest request, HttpContext http,
                ApplicationDeploymentCoordinator coordinator, ApplicationDeploymentManager manager) => Handle(() =>
            {
                if (!request.Confirmed) return Problem(ApplicationDeploymentProblemCodes.ConfirmationRequired, 400);
                _ = manager.Require(applicationId);
                var operation = coordinator.Start(
                    new DeploymentRequest(applicationId, DeploymentOperationKind.Rollback, RevisionId: request.RevisionId),
                    Actor(http.User), Key(http));
                return Accepted(operation);
            }))
            .RequireAuthorization(ManagePolicy);

        group.MapPost(ApplicationDeploymentApiRoutes.StartPattern,
            (Guid applicationId, ApplicationLifecycleRequest request, HttpContext http,
                ApplicationDeploymentCoordinator coordinator, ApplicationDeploymentManager manager) =>
                Lifecycle(applicationId, DeploymentOperationKind.Start, request, http, coordinator, manager))
            .RequireAuthorization(ManagePolicy);

        group.MapPost(ApplicationDeploymentApiRoutes.StopPattern,
            (Guid applicationId, ApplicationLifecycleRequest request, HttpContext http,
                ApplicationDeploymentCoordinator coordinator, ApplicationDeploymentManager manager) =>
                Lifecycle(applicationId, DeploymentOperationKind.Stop, request, http, coordinator, manager))
            .RequireAuthorization(ManagePolicy);

        group.MapPost(ApplicationDeploymentApiRoutes.RestartPattern,
            (Guid applicationId, ApplicationLifecycleRequest request, HttpContext http,
                ApplicationDeploymentCoordinator coordinator, ApplicationDeploymentManager manager) =>
                Lifecycle(applicationId, DeploymentOperationKind.Restart, request, http, coordinator, manager))
            .RequireAuthorization(ManagePolicy);

        group.MapGet(ApplicationDeploymentApiRoutes.OperationPattern,
            (Guid operationId, ApplicationDeploymentCoordinator coordinator) => Handle(() =>
                coordinator.Get(operationId) is { } operation ? Results.Ok(operation) : Results.NotFound()))
            .RequireAuthorization(ReadPolicy);

        group.MapGet(ApplicationDeploymentApiRoutes.ActiveOperationPattern,
            (Guid applicationId, ApplicationDeploymentCoordinator coordinator) => Handle(() =>
                coordinator.GetActive(applicationId) is { } operation ? Results.Ok(operation) : Results.NotFound()))
            .RequireAuthorization(ReadPolicy);

        group.MapPost(ApplicationDeploymentApiRoutes.CancelOperationPattern,
            (Guid operationId, HttpContext http, ApplicationDeploymentCoordinator coordinator) => Handle(() =>
            {
                // A cancel is itself retryable, so it carries the same key requirement as the action
                // it is trying to stop.
                return Results.Ok(coordinator.Cancel(operationId, Key(http)));
            }))
            .RequireAuthorization(ManagePolicy);

        // A DELETE request does not infer a complex parameter as a body, so the binding is explicit.
        group.MapDelete(ApplicationDeploymentApiRoutes.ApplicationPattern,
            (Guid applicationId, [Microsoft.AspNetCore.Mvc.FromBody] DeleteApplicationRequest request, HttpContext http,
                ApplicationDeploymentCoordinator coordinator, ApplicationDeploymentManager manager) => Handle(() =>
            {
                if (request.DeleteVolumes && !request.Confirmed)
                    return Problem(ApplicationDeploymentProblemCodes.ConfirmationRequired, 400);
                _ = manager.Require(applicationId);
                var operation = coordinator.Start(
                    new DeploymentRequest(applicationId, DeploymentOperationKind.Delete, DeleteVolumes: request.DeleteVolumes),
                    Actor(http.User), Key(http));
                return Accepted(operation);
            }))
            .RequireAuthorization(ManagePolicy);

        // --- Staging input -------------------------------------------------------------------
        group.MapPost(ApplicationDeploymentApiRoutes.UploadPattern,
            async (HttpContext http, ApplicationDeploymentStagingStore staging) => await HandleAsync(async () =>
            {
                if (!http.Request.HasFormContentType) return Problem(ApplicationDeploymentProblemCodes.InvalidRequest, 415);
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                var file = form.Files.FirstOrDefault();
                if (file is null || file.Length == 0) return Problem(ApplicationDeploymentProblemCodes.ArchiveUnavailable, 400);
                await using var stream = file.OpenReadStream();
                var staged = await staging.StageAsync(file.FileName, stream, Actor(http.User), http.RequestAborted);
                return Results.Ok(staged);
            }))
            .RequireAuthorization(ManagePolicy)
            .DisableAntiforgery();

        group.MapPost(ApplicationDeploymentApiRoutes.FileReferencePattern,
            (CreateDeploymentFileReferenceRequest request, HttpContext http, ApplicationDeploymentStagingStore staging) => Handle(() =>
                Results.Ok(staging.Register(request.Path, Actor(http.User)))))
            .RequireAuthorization(ManagePolicy);

        return app;
    }

    private static Task<IResult> Lifecycle(Guid applicationId, DeploymentOperationKind kind, ApplicationLifecycleRequest request,
        HttpContext http, ApplicationDeploymentCoordinator coordinator, ApplicationDeploymentManager manager) => HandleAsync(() =>
    {
        if (request.Force && !request.Confirmed) return Task.FromResult(Problem(ApplicationDeploymentProblemCodes.ConfirmationRequired, 400));
        _ = manager.Require(applicationId);
        var operation = coordinator.Start(new DeploymentRequest(applicationId, kind, Force: request.Force),
            Actor(http.User), Key(http));
        return Task.FromResult(Accepted(operation));
    });

    /// <summary>An idempotency key is mandatory for every action that can create a resource.</summary>
    private static string Key(HttpContext http) => http.Request.Headers["Idempotency-Key"].ToString();

    private static string RequestReference<T>(T request) => ApplicationDeploymentValidation.Reference(
        System.Text.Json.JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default));

    /// <summary>The actor is a stable identifier, never a display name, and is only ever stored hashed.</summary>
    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException();

    private static IResult Accepted(DeploymentOperationDto operation) =>
        Results.Accepted(ApplicationDeploymentApiRoutes.Operation(operation.OperationId), operation);

    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (ApplicationDeploymentException error) { return Problem(error.ProblemCode, error.StatusCode); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ApplicationDeploymentException error) { return Problem(error.ProblemCode, error.StatusCode); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }

    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code,
        type: "https://relaxkonos.app/problems/" + code, extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
