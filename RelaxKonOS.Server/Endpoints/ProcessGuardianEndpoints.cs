using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.ProcessGuardian;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Endpoints;

public static class ProcessGuardianEndpoints
{
    public static IEndpointRouteBuilder MapProcessGuardianEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/guardian").RequireAuthorization().WithTags("Process Guardian");
        group.MapGet("/status", (RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapGet("/workloads", (RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => ReadAsync(() => service.ListWorkloadsAsync(ct)));
        group.MapGet("/workloads/{id}", (string id, RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.GetDefinitionAsync(id, ct));
        group.MapPost("/workloads", (
            UpsertGuardianWorkloadRequest request,
            HttpContext http,
            IUserExecutionContextResolver executionContexts,
            RelaxKonOS.Server.ProcessGuardian.IRunAsAuthorizationService runAs,
            RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service,
            CancellationToken ct) =>
        {
            string requester;
            try { requester = executionContexts.Resolve(http.User).Identity.CanonicalAccount; }
            catch (UserExecutionException exception)
            { return Task.FromResult(new GuardianAgentResponse(false, "guardian." + exception.ProblemCode.ToString().ToLowerInvariant())); }
            var authorization = runAs.Authorize(requester, request.Definition.RunAs, request.RunAsApproval);
            if (!authorization.Success)
                return Task.FromResult(new GuardianAgentResponse(false, authorization.ProblemCode));
            return service.UpsertAsync(request.Definition with
            {
                RunAs = authorization.RunAs,
                RunAsIdentity = authorization.StableIdentity,
            }, ct);
        });
        group.MapDelete("/workloads/{id}", (string id, RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.DeleteAsync(id, ct));
        group.MapPost("/workloads/{id}/{action}", (string id, string action, RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ApplyActionAsync(id, action, ct));
        group.MapGet("/workloads/{id}/logs", (string id, RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => ReadAsync(() => service.ListLogsAsync(id, ct)));
        group.MapGet("/audit", (RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => ReadAsync(() => service.ListAuditAsync(ct)));
        group.MapGet("/scripts", (HttpContext http, IUserExecutionContextResolver contexts,
            RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) =>
        {
            var identity = ResolveScriptIdentity(http, contexts);
            return identity is null ? Task.FromResult(new GuardianAgentResponse(false, "guardian.script_identity_unavailable"))
                : service.ListScriptsAsync(identity.Value.Stable, ct);
        });
        group.MapGet("/scripts/{id}", (string id, HttpContext http, IUserExecutionContextResolver contexts,
            RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) =>
        {
            var identity = ResolveScriptIdentity(http, contexts);
            return identity is null ? Task.FromResult(new GuardianAgentResponse(false, "guardian.script_identity_unavailable"))
                : service.GetScriptAsync(identity.Value.Stable, id, ct);
        });
        group.MapPost("/scripts", (SubmitScriptTaskRequest request, HttpContext http, IUserExecutionContextResolver contexts,
            RelaxKonOS.Server.ProcessGuardian.IRunAsAuthorizationService runAs,
            RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) =>
        {
            var identity = ResolveScriptIdentity(http, contexts);
            if (identity is null) return Task.FromResult(new GuardianAgentResponse(false, "guardian.script_identity_unavailable"));
            if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var id))
                return Task.FromResult(new GuardianAgentResponse(false, "guardian.script_idempotency_required"));
            if (string.IsNullOrWhiteSpace(request.ExecutablePath) || string.IsNullOrWhiteSpace(request.WorkingDirectory) ||
                request.Arguments is null || request.Environment is null || request.Arguments.Count > 64 ||
                request.Environment.Count > 32 || request.TimeoutSeconds is < 1 or > 3600)
                return Task.FromResult(new GuardianAgentResponse(false, "guardian.script_invalid"));
            var approval = runAs.Authorize(identity.Value.Account, request.RunAs, request.RunAsApproval);
            if (!approval.Success) return Task.FromResult(new GuardianAgentResponse(false, approval.ProblemCode));
            return service.SubmitScriptAsync(new ScriptTaskDefinitionDto(id.ToString("D"), identity.Value.Stable,
                request.ExecutablePath, request.Arguments, request.WorkingDirectory, request.Environment,
                request.TimeoutSeconds, approval.RunAs!, approval.StableIdentity!), ct);
        });
        group.MapPost("/scripts/{id}/cancel", (string id, HttpContext http, IUserExecutionContextResolver contexts,
            RelaxKonOS.Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) =>
        {
            var identity = ResolveScriptIdentity(http, contexts);
            return identity is null ? Task.FromResult(new GuardianAgentResponse(false, "guardian.script_identity_unavailable"))
                : service.CancelScriptAsync(identity.Value.Stable, id, ct);
        });
        group.MapGet("/services", (RelaxKonOS.Server.ProcessGuardian.INativeServiceAdapter services, CancellationToken ct) => services.ListAsync(ct))
            .AddEndpointFilter(new ServerModeEndpointFilter(ServerHostFeature.NativeServices));
        group.MapPost("/services/{id}/{action}", async (string id, string action, NativeServiceActionRequest request, HttpContext http,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.ProcessGuardian.INativeServiceAdapter services, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(http.User, HostElevationCapability.NativeServiceAction, id))
                return Results.Problem(statusCode: 403, title: "需要管理员权限", detail: "此服务操作需要当前会话的管理员授权。",
                    type: "https://relaxkonos.app/problems/elevation-required");
            return Results.Ok(await services.ApplyActionAsync(id, action, request, ct));
        }).AddEndpointFilter(new ServerModeEndpointFilter(ServerHostFeature.NativeServices));
        group.MapPost("/agent/installation/plan", (RelaxKonOS.Server.ProcessGuardian.IGuardianAgentInstaller installer, CancellationToken ct) => installer.CreatePlanAsync(ct))
            .AddEndpointFilter(new ServerModeEndpointFilter(ServerHostFeature.AgentInstallation));
        return app;
    }

    private static async Task<IResult> ReadAsync<T>(Func<Task<IReadOnlyList<T>>> read)
    {
        try { return Results.Ok(await read()); }
        catch (RelaxKonOS.Server.ProcessGuardian.GuardianReadException exception)
        {
            return Results.Problem(statusCode: exception.ProblemCode == "guardian.workload_not_found" ? 404 : 503,
                title: "Guardian read failed", extensions: new Dictionary<string, object?> { ["problemCode"] = exception.ProblemCode });
        }
    }

    private static (string Account, string Stable)? ResolveScriptIdentity(HttpContext http, IUserExecutionContextResolver contexts)
    {
        try
        {
            var identity = contexts.Resolve(http.User).Identity;
            return (identity.CanonicalAccount, identity.StableIdentity);
        }
        catch (UserExecutionException) { return null; }
    }
}
