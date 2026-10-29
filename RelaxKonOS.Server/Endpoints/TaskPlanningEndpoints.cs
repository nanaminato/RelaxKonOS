using System.Security.Claims;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.TaskOrchestration;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Server.Endpoints;

public static class TaskPlanningEndpoints
{
    public static IEndpointRouteBuilder MapTaskPlanningEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("").RequireHostFeature(ServerHostFeature.PrivilegedOperations)
            .MapPost(TaskPlanningRoutes.Preview, PreviewAsync)
            .RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy).WithTags("Task planning");
        app.MapGroup("").RequireHostFeature(ServerHostFeature.PrivilegedOperations).RequireHostFeature(ServerHostFeature.AgentInstallation)
            .MapPost(TaskPlanningRoutes.Create, PlanChangeAsync)
            .RequireAuthorization(RuntimeCatalogEndpoints.ManagePolicy).WithTags("Task planning");
        app.MapGet(TaskPlanningRoutes.PlanPattern, GetPlan)
            .RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy).WithTags("Task planning");
        app.MapPost(TaskPlanningRoutes.InvalidatePattern, InvalidateAsync)
            .RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy).WithTags("Task planning");
        return app;
    }

    private static async Task<IResult> PreviewAsync(HttpContext http, IRuntimeTaskPreviewService service, TaskPlanStore store, CancellationToken ct)
    {
        var subject = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject)) return Results.Forbid();
        if (!http.Request.HasJsonContentType()) return Problem("task.content_type_invalid", 415);
        if (http.Request.ContentLength > TaskPlanLimits.Default.MaximumPlanBytes)
            return Problem("task.intent_size_exceeded", 413);
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await http.Request.Body.ReadAsync(chunk, ct);
                if (read == 0) break;
                if (buffer.Length + read > TaskPlanLimits.Default.MaximumPlanBytes)
                    return Problem("task.intent_size_exceeded", 413);
                buffer.Write(chunk, 0, read);
            }
            var intent = TaskPlanJson.ReadIntent(buffer.ToArray());
            var preview = await service.PreviewAsync(subject, intent, ct);
            store.Save(preview, subject);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(preview);
        }
        catch (TaskPlanningException ex) { return Problem(ex.ProblemCode, ex.ProblemCode.EndsWith("size_exceeded", StringComparison.Ordinal) ? 413 : 400); }
        catch (TaskPlanStoreException ex) { return Problem(ex.ProblemCode, ex.StatusCode); }
        catch (RuntimeCatalogException) { return Problem("task.observation_unavailable", 503); }
        catch (RuntimeLifecycleException) { return Problem("task.observation_unavailable", 503); }
        catch (IOException) when (!ct.IsCancellationRequested) { return Problem("task.observation_unavailable", 503); }
    }

    private static IResult GetPlan(Guid planId, HttpContext http, TaskPlanStore store)
    {
        var subject = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject)) return Results.Forbid();
        http.Response.Headers.CacheControl = "no-store";
        try { return store.Get(planId, subject) is { } record ? Results.Ok(record) : Problem("task.plan_not_found", 404); }
        catch (TaskPlanStoreException ex) { return Problem(ex.ProblemCode, ex.StatusCode); }
    }

    private static async Task<IResult> PlanChangeAsync(HttpContext http, IRuntimeTaskChangePlanningService service,
        TaskPlanStore store, CancellationToken ct)
    {
        if (!InstallationEndpoints.CanInstall(http.User, InstallationServiceId.SingBox)) return Results.Forbid();
        var subject = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject)) return Results.Forbid();
        if (!http.Request.HasJsonContentType()) return Problem("task.content_type_invalid", 415);
        if (http.Request.ContentLength > TaskPlanLimits.Default.MaximumPlanBytes) return Problem("task.intent_size_exceeded", 413);
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var read = await http.Request.Body.ReadAsync(bytes, ct);
                if (read == 0) break;
                if (buffer.Length + read > TaskPlanLimits.Default.MaximumPlanBytes) return Problem("task.intent_size_exceeded", 413);
                buffer.Write(bytes, 0, read);
            }
            var intent = TaskPlanJson.ReadIntent(buffer.ToArray());
            var plan = await service.PlanAsync(subject, intent, ct);
            store.Save(plan, subject);
            return Results.Ok(plan);
        }
        catch (TaskPlanningException ex) { return Problem(ex.ProblemCode, ex.ProblemCode.EndsWith("size_exceeded", StringComparison.Ordinal) ? 413 : 400); }
        catch (TaskPlanStoreException ex) { return Problem(ex.ProblemCode, ex.StatusCode); }
        catch (RuntimeLifecycleException) { return Problem("task.observation_unavailable", 503); }
        catch (IOException) when (!ct.IsCancellationRequested) { return Problem("task.observation_unavailable", 503); }
    }

    private static async Task<IResult> InvalidateAsync(Guid planId, HttpContext http, TaskPlanStore store, CancellationToken ct)
    {
        var subject = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject)) return Results.Forbid();
        if (!http.Request.HasJsonContentType()) return Problem("task.content_type_invalid", 415);
        if (http.Request.ContentLength > 4096) return Problem("task.intent_size_exceeded", 413);
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[1024];
            while (true)
            {
                var read = await http.Request.Body.ReadAsync(bytes, ct);
                if (read == 0) break;
                if (buffer.Length + read > 4096) return Problem("task.intent_size_exceeded", 413);
                buffer.Write(bytes, 0, read);
            }
            var request = TaskPlanJson.ReadInvalidation(buffer.ToArray());
            return Results.Ok(store.Invalidate(planId, subject, request.PlanDigest, request.ExpectedRevision));
        }
        catch (TaskPlanningException ex) { return Problem(ex.ProblemCode, ex.ProblemCode.EndsWith("size_exceeded", StringComparison.Ordinal) ? 413 : 400); }
        catch (TaskPlanStoreException ex) { return Problem(ex.ProblemCode, ex.StatusCode); }
    }

    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
