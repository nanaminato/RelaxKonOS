using System.Security.Claims;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Endpoints;

internal static class ModulePrivilegeEndpoints
{
    internal const string Root = ServerModulePrivilegeRoutes.Plans;
    internal static IEndpointRouteBuilder MapModulePrivilegeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ServerModulePrivilegeRoutes.Trust, (string moduleId, ServerModuleTrustRequest request,
            HttpContext http, ServerModuleContext context, ModuleManagementStore management) =>
            CoreModulePrivilegeBroker.Respond(() =>
            {
                if (!management.Writable) throw new ModulePrivilegeException("module.privilege_denied", 403);
                var installed = InstalledModuleStore.Read(context.ContentRootPath).SingleOrDefault(m => m.ModuleId == moduleId);
                if (installed is null || installed.Runtime != "module-host" || installed.ArtifactSha256 != request.ArtifactSha256
                    || installed.ManifestSha256 != request.ManifestSha256) throw new ModulePrivilegeException("module.privilege_plan_changed");
                InstalledModuleStore.VerifyPackage(context.ContentRootPath, installed);
                return Task.FromResult<IResult>(Results.Ok(ModuleTrustStore.Set(context.ContentRootPath, installed,
                    http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", request.Trusted)));
            })).RequireAuthorization(ServerModuleManagementEndpoints.ManagePolicy);
        app.MapGet(Root + "/{id:guid}", (Guid id, HttpContext http, CoreModulePrivilegeBroker broker) =>
            CoreModulePrivilegeBroker.Respond(async () => Results.Ok(await broker.Plans.ReadForUserAsync(id,
                CoreModulePrivilegeBroker.Session(http.User))))).RequireAuthorization();
        app.MapPost(Root + "/{id:guid}/cancel", (Guid id, HttpContext http, CoreModulePrivilegeBroker broker) =>
            CoreModulePrivilegeBroker.Respond(async () => Results.Ok(await broker.Plans.CancelAsync(id,
                CoreModulePrivilegeBroker.Session(http.User))))).RequireAuthorization();
        app.MapPost(Root + "/{id:guid}/approve", (Guid id, ModulePrivilegeApproval approval, HttpContext http,
            CoreModulePrivilegeBroker broker, IHostAdministratorAuthenticator administrators, IHostElevationSessionStore elevations,
            OwnerDeviceKeyService owners, HostElevationSessionState state, IPrivilegedOperationTransport helper) =>
            CoreModulePrivilegeBroker.Respond(async () =>
            {
                if (!state.IsSessionValid(http.User)) throw new ModulePrivilegeException("module.privilege_denied", 403);
                var result = await broker.Plans.ApproveAsync(id, CoreModulePrivilegeBroker.Session(http.User), approval,
                    () =>
                    {
                        if (owners.IsOwner(http.User) || state.IsAdministratorVerified(http.User)) return Task.FromResult(true);
                        var current = http.User.FindFirstValue("name") ?? http.User.Identity?.Name;
                        if (string.IsNullOrEmpty(current)) return Task.FromResult(false);
                        var authentication = administrators.Authenticate(current, approval.AdministratorUsername, approval.Password);
                        if (!authentication.Succeeded) return Task.FromResult(false);
                        // Remember verified identity only. The durable plan gate still requires explicit consent every time.
                        elevations.Grant(http.User, HostElevationCapability.NativeServiceAction, "module-broker",
                            false, authentication.AuthenticationMethod, http.TraceIdentifier);
                        return Task.FromResult(true);
                    }, async (operationId, kind, target) =>
                    {
                        var action = kind switch
                        {
                            "service-start" => PrivilegedServiceAction.Start,
                            "service-stop" => PrivilegedServiceAction.Stop,
                            "service-restart" => PrivilegedServiceAction.Restart,
                            _ => throw new ModulePrivilegeException("module.privilege_unsupported", 400),
                        };
                        var outcome = await helper.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NativeServiceAction,
                            ServiceId: target, ServiceAction: action, Correlation: CorrelationContext.Create(operationId)), CancellationToken.None);
                        if (outcome.ProblemCode is PrivilegedProblemCode.HelperUnavailable or PrivilegedProblemCode.TimedOut or PrivilegedProblemCode.InvalidProtocol)
                            throw new IOException("module.privilege_helper_response_unknown");
                        return outcome.Success;
                    });
                return Results.Ok(result);
            })).RequireAuthorization();
        return app;
    }
}
