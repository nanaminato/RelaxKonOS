using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RemoteOS.Protocol.ProcessGuardian;
using RemoteOS.Protocol.Privileged;
using Server.Privileged;

namespace Server.Endpoints;

public static class ProcessGuardianEndpoints
{
    public static IEndpointRouteBuilder MapProcessGuardianEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"/{RemoteOS.Protocol.Common.RemoteOsEndpoints.ApiVersionPrefix}/guardian").RequireAuthorization().WithTags("Process Guardian");
        group.MapGet("/status", (Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapGet("/workloads", (Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ListWorkloadsAsync(ct));
        group.MapGet("/workloads/{id}", (string id, Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.GetDefinitionAsync(id, ct));
        group.MapPost("/workloads", (
            UpsertGuardianWorkloadRequest request,
            HttpContext http,
            Server.ProcessGuardian.IRunAsAuthorizationService runAs,
            Server.ProcessGuardian.IProcessGuardianService service,
            CancellationToken ct) =>
        {
            var requester = http.User.FindFirst(JwtRegisteredClaimNames.Name)?.Value
                            ?? http.User.FindFirst(ClaimTypes.Name)?.Value
                            ?? string.Empty;
            var authorization = runAs.Authorize(requester, request.Definition.RunAs, request.RunAsApproval);
            if (!authorization.Success)
                return Task.FromResult(new GuardianAgentResponse(false, authorization.ProblemCode));
            return service.UpsertAsync(request.Definition with { RunAs = authorization.RunAs }, ct);
        });
        group.MapDelete("/workloads/{id}", (string id, Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.DeleteAsync(id, ct));
        group.MapPost("/workloads/{id}/{action}", (string id, string action, Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ApplyActionAsync(id, action, ct));
        group.MapGet("/workloads/{id}/logs", (string id, Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ListLogsAsync(id, ct));
        group.MapGet("/audit", (Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ListAuditAsync(ct));
        group.MapGet("/services", (Server.ProcessGuardian.INativeServiceAdapter services, CancellationToken ct) => services.ListAsync(ct));
        group.MapPost("/services/{id}/{action}", async (string id, string action, NativeServiceActionRequest request, HttpContext http,
            IHostElevationSessionStore elevations, Server.ProcessGuardian.INativeServiceAdapter services, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(http.User, HostElevationCapability.NativeServiceAction, id))
                return Results.Problem(statusCode: 403, title: "需要管理员权限", detail: "此服务操作需要当前会话的管理员授权。",
                    type: "https://remoteos.app/problems/elevation-required");
            return Results.Ok(await services.ApplyActionAsync(id, action, request, ct));
        });
        group.MapPost("/agent/installation/plan", (Server.ProcessGuardian.IGuardianAgentInstaller installer, CancellationToken ct) => installer.CreatePlanAsync(ct));
        group.MapPost("/agent/installation/execute", (GuardianInstallationExecutionRequest request, Server.ProcessGuardian.IGuardianAgentInstaller installer, CancellationToken ct) => installer.ExecuteAsync(request, ct));
        return app;
    }
}
