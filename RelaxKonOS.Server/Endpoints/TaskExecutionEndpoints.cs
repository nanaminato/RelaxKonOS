using System.Security.Claims;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.TaskOrchestration;

namespace RelaxKonOS.Server.Endpoints;

public static class TaskExecutionEndpoints
{
    public static IEndpointRouteBuilder MapTaskExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var writes = app.MapGroup("").RequireAuthorization(RuntimeCatalogEndpoints.ManagePolicy)
            .RequireHostFeature(ServerHostFeature.PrivilegedOperations).RequireHostFeature(ServerHostFeature.AgentInstallation)
            .WithTags("Task execution");
        writes.MapPost(TaskPlanningRoutes.ApprovePattern, ApproveAsync).RequireRateLimiting("login");
        writes.MapPost(TaskPlanningRoutes.StartRunPattern, StartAsync);
        writes.MapPost(TaskPlanningRoutes.AdvancePattern, AdvanceAsync);
        writes.MapPost(TaskPlanningRoutes.CancelRunPattern, CancelAsync);
        app.MapGet(TaskPlanningRoutes.RunPattern, GetRun).RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy).WithTags("Task execution");
        app.MapGet(TaskPlanningRoutes.ApprovalPattern, GetApproval).RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy).WithTags("Task execution");
        return app;
    }
    private static string Actor(HttpContext http)
    {
        var subjects = http.User.Claims.Where(item => item.Type is "sub" or ClaimTypes.NameIdentifier)
            .Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
        return subjects is [var subject] && !string.IsNullOrWhiteSpace(subject)
            ? subject : throw new TaskApprovalException("task.permission_denied");
    }
    private static string Key(HttpContext http)
    {
        var header = http.Request.Headers["Idempotency-Key"];
        if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0]) || header[0]!.Length > 128 || header[0]!.Any(c => c < 33 || c > 126))
            throw new TaskApprovalException("task.idempotency_required", 400);
        return header[0]!;
    }
    private static void Management(HttpContext http)
    {
        if (!InstallationEndpoints.CanInstall(http.User, InstallationServiceId.SingBox)) throw new TaskApprovalException("task.permission_denied");
        Actor(http);
    }
    private static async Task<byte[]> Body(HttpContext http, int maximum, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType()) throw new TaskApprovalException("task.content_type_invalid", 415);
        if (http.Request.ContentLength > maximum) throw new TaskApprovalException("task.intent_size_exceeded", 413);
        using var buffer = new MemoryStream(); var bytes = new byte[1024];
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(bytes, ct); if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximum) throw new TaskApprovalException("task.intent_size_exceeded", 413);
            buffer.Write(bytes, 0, read);
        }
    }
    private static Task<IResult> ApproveAsync(Guid planId, HttpContext http, TaskPlanApprovalService service, CancellationToken ct) => Guard(http, async () =>
    {
        Management(http); var key = Key(http); var request = TaskPlanJson.ReadApproval(await Body(http, 16384, ct));
        return Results.Ok(await service.ApproveAsync(http.User, planId, request.PlanDigest, request.Confirmed, key,
            request.AdministratorUsername, request.Password, ct));
    }, ct);
    private static Task<IResult> StartAsync(Guid planId, HttpContext http, TaskRunStartService service, CancellationToken ct) => Guard(http, async () =>
    {
        Management(http); var key = Key(http); var request = TaskPlanJson.ReadStart(await Body(http, 4096, ct));
        var run = await service.StartAsync(http.User, planId, request, key, ct);
        return Results.Accepted(TaskPlanningRoutes.Run(run.RunId), run);
    }, ct);
    private static Task<IResult> AdvanceAsync(Guid runId, HttpContext http, TaskRunAdvancementService service, CancellationToken ct) => Guard(http, async () =>
    {
        Management(http); var request = TaskPlanJson.ReadRunRevision(await Body(http, 4096, ct));
        return Results.Ok(await service.AdvanceAsync(http.User, runId, request.ExpectedRevision, ct));
    }, ct);
    private static Task<IResult> CancelAsync(Guid runId, HttpContext http, TaskRunCancellationService service, CancellationToken ct) => Guard(http, async () =>
    {
        Management(http); var request = TaskPlanJson.ReadRunRevision(await Body(http, 4096, ct));
        return Results.Ok(await service.RequestAsync(http.User, runId, request.ExpectedRevision, ct));
    }, ct);
    private static Task<IResult> GetRun(Guid runId, HttpContext http, TaskPlanStore store) => Guard(http,
        () => Task.FromResult(store.GetRun(runId, Actor(http)) is { } run ? Results.Ok(run) : Problem("task.run_not_found", 404)), default);
    private static Task<IResult> GetApproval(Guid approvalId, HttpContext http, TaskPlanStore store) => Guard(http,
        () => Task.FromResult(store.GetApproval(approvalId, Actor(http)) is { } approval ? Results.Ok(approval) : Problem("task.approval_not_found", 404)), default);
    private static async Task<IResult> Guard(HttpContext http, Func<Task<IResult>> action, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (TaskApprovalException ex) { return Problem(ex.ProblemCode, ex.StatusCode); }
        catch (TaskPlanStoreException ex) { return Problem(ex.ProblemCode, ex.StatusCode); }
        catch (TaskPlanningException ex) { return Problem(ex.ProblemCode, ex.ProblemCode.EndsWith("size_exceeded", StringComparison.Ordinal) ? 413 : 400); }
        catch (InstallationException) { return Problem("task.domain_unavailable", 503); }
        catch (Exception ex) when (ex is RuntimeLifecycleException or RuntimeCatalogException || ex is IOException && !ct.IsCancellationRequested)
        { return Problem("task.observation_unavailable", 503); }
    }
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
