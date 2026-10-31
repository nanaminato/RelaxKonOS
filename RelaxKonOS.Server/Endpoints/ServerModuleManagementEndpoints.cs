using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Endpoints;

internal static class ServerModuleManagementEndpoints
{
    internal const string ManagePolicy = "ServerModulesManage";
    internal static IEndpointRouteBuilder MapServerModuleManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization().WithTags("Server modules");
        group.MapGet(ServerModuleManagementRoutes.Detail, async (string moduleId, HttpContext http,
            ServerModules modules, ServerModuleContext context, CancellationToken ct) => await Respond(async () =>
        {
            var module = (await modules.DescribeAsync(http.RequestServices, context, ct)).SingleOrDefault(x => x.Id == moduleId);
            return module is null ? Problem("module-not-found", 404) : Results.Ok(ForActor(module, http.User, context.Configuration));
        }));
        group.MapPost(ServerModuleManagementRoutes.Plans, async (ServerModulePlanRequest request, HttpContext http,
            ModuleManagementStore store, ServerModules modules, ServerModuleContext context, CancellationToken ct) =>
            await Respond(async () => Results.Ok(store.Preview(Actor(http.User), request,
                await modules.DescribeAsync(http.RequestServices, context, ct)))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.Plan, (Guid planId, HttpContext http, ModuleManagementStore store) =>
            Respond(() => Task.FromResult<IResult>(Results.Ok(store.PlanStatus(Actor(http.User), planId)))))
            .RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.Apply, async (Guid planId, HttpContext http,
            ModuleManagementExecutor executor, ServerModules modules, ServerModuleContext context, CancellationToken ct) =>
            await Respond(async () => Results.Ok(executor.Apply(Actor(http.User), planId,
                await modules.DescribeAsync(http.RequestServices, context, ct)))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.Operation, (Guid operationId, HttpContext http, ModuleManagementStore store) =>
            Respond(() => Task.FromResult<IResult>(Results.Ok(store.Operation(Actor(http.User), operationId)))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.Operations, (string? moduleId, int? offset, int? limit,
            HttpContext http, ModuleManagementStore store) => Respond(() => Task.FromResult<IResult>(
                Results.Ok(store.Operations(Actor(http.User), moduleId, offset ?? 0, limit ?? 25)))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.Releases, (string moduleId, ServerModules modules) =>
            modules.KnownModuleIds.Contains(moduleId) ? Problem("module-action-unsupported", 409) : Problem("module-not-found", 404));
        return app;
    }

    internal static bool CanManage(ClaimsPrincipal user, IConfiguration configuration)
    {
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(subject, out var id) && configuration.GetSection("ModuleManagement:Administrators")
            .GetChildren().Any(entry => Guid.TryParse(entry.Value, out var administrator) && administrator == id);
    }
    internal static ServerModuleDto ForActor(ServerModuleDto module, ClaimsPrincipal actor, IConfiguration configuration) => CanManage(actor, configuration) ? module :
        module with { AllowedActions = Enum.GetValues<ServerModuleAction>().Select(action =>
            new ServerModuleActionAdmission(action, false, [new("module-forbidden")])).ToArray() };
    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub") ?? throw new ModuleManagementException("module-forbidden", 403);
    private static async Task<IResult> Respond(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ModuleManagementException exception) { return Problem(exception.Code, exception.Status); }
        catch (Exception exception) when (exception is IOException or System.UnauthorizedAccessException)
        { return Problem("module-state-unavailable", 503); }
    }
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status,
        type: "https://relaxkonos.app/problems/" + code, title: code,
        extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
