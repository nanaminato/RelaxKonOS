using System.Security.Claims;
using RemoteOS.Protocol.Files;
using Server.Files;

namespace Server.Endpoints;

public static class FileOperationEndpoints
{
    public static void MapFileOperationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(FileApiRoutes.Operations).RequireAuthorization(FileAuthorizationPolicies.Manage).WithTags("Files");
        group.MapPost("", (StartFileOperationRequest request, HttpContext http, FileOperationService jobs) =>
            Execute(http.User, owner => Results.Ok(jobs.Start(owner, http.User, request))));
        group.MapGet("", (HttpContext http, FileOperationService jobs) =>
            Execute(http.User, owner => Results.Ok(jobs.List(owner))));
        group.MapGet("/{id:guid}", (Guid id, HttpContext http, FileOperationService jobs) =>
            Execute(http.User, owner => Results.Ok(jobs.Get(owner, id))));
        group.MapPost("/{id:guid}/cancel", (Guid id, HttpContext http, FileOperationService jobs) =>
            Execute(http.User, owner => Results.Ok(jobs.Cancel(owner, id))));
        group.MapPost("/{id:guid}/decision", (Guid id, FileOperationDecisionRequest request, HttpContext http, FileOperationService jobs) =>
            Execute(http.User, owner => Results.Ok(jobs.Decide(owner, id, request, http.User))));
    }
    private static IResult Execute(ClaimsPrincipal user, Func<string, IResult> action)
    {
        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        var workspace = user.FindFirstValue("workspace_id");
        var device = user.FindFirstValue("device_id");
        if (!Guid.TryParse(subject, out var userId) || !Guid.TryParse(workspace, out var workspaceId)
            || !Guid.TryParse(device, out var deviceId)) return Results.Unauthorized();
        try { return action($"{userId:N}/{workspaceId:N}/{deviceId:N}"); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid operation", detail: ex.Message); }
        catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Operation unavailable", detail: ex.Message); }
    }
}
