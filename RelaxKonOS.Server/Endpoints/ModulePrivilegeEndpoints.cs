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
        app.MapPost(ServerModulePrivilegeRoutes.Consents, (ModulePrivilegeConsentRequest request, HttpContext http,
            CoreModulePrivilegeBroker broker, ModuleManagementStore management, HostElevationSessionState state) =>
            CoreModulePrivilegeBroker.Respond(async () =>
            {
                EnsureConsentManagement(http, management, state);
                return Results.Ok(await broker.Consents.PrepareAsync(CoreModulePrivilegeBroker.Session(http.User), Actor(http.User), request));
            })).RequireAuthorization(ServerModuleManagementEndpoints.ManagePolicy);
        app.MapGet(ServerModulePrivilegeRoutes.Consents, (HttpContext http, CoreModulePrivilegeBroker broker,
            ModuleManagementStore management, HostElevationSessionState state) => CoreModulePrivilegeBroker.Respond(async () =>
            {
                EnsureConsentManagement(http, management, state);
                return Results.Ok(await broker.Consents.ListAsync());
            })).RequireAuthorization(ServerModuleManagementEndpoints.ManagePolicy);
        app.MapGet(ServerModulePrivilegeRoutes.Consents + "/{id:guid}", (Guid id, HttpContext http, CoreModulePrivilegeBroker broker,
            ModuleManagementStore management, HostElevationSessionState state) => CoreModulePrivilegeBroker.Respond(async () =>
            {
                EnsureConsentManagement(http, management, state);
                return Results.Ok(await broker.Consents.ReadAsync(id, CoreModulePrivilegeBroker.Session(http.User)));
            })).RequireAuthorization(ServerModuleManagementEndpoints.ManagePolicy);
        app.MapPost(ServerModulePrivilegeRoutes.Consents + "/{id:guid}/revoke", (Guid id, HttpContext http, CoreModulePrivilegeBroker broker,
            ModuleManagementStore management, HostElevationSessionState state) => CoreModulePrivilegeBroker.Respond(async () =>
            {
                EnsureConsentManagement(http, management, state);
                return Results.Ok(await broker.Consents.RevokeAsync(id, Actor(http.User)));
            })).RequireAuthorization(ServerModuleManagementEndpoints.ManagePolicy);
        app.MapPost(ServerModulePrivilegeRoutes.Consents + "/{id:guid}/approve", (Guid id, ModulePrivilegeApproval approval, HttpContext http,
            CoreModulePrivilegeBroker broker, ModuleManagementStore management, IHostAdministratorAuthenticator administrators,
            IHostElevationSessionStore elevations, OwnerDeviceKeyService owners, HostElevationSessionState state) =>
            CoreModulePrivilegeBroker.Respond(async () =>
            {
                EnsureConsentManagement(http, management, state);
                return Results.Ok(await broker.Consents.ApproveAsync(id, CoreModulePrivilegeBroker.Session(http.User), approval,
                    () => VerifyAdministrator(http, approval, administrators, elevations, owners, state)));
            })).RequireAuthorization(ServerModuleManagementEndpoints.ManagePolicy);
        app.MapPost(Root + "/{id:guid}/approve", (Guid id, ModulePrivilegeApproval approval, HttpContext http,
            CoreModulePrivilegeBroker broker, IHostAdministratorAuthenticator administrators, IHostElevationSessionStore elevations,
            OwnerDeviceKeyService owners, HostElevationSessionState state, IPrivilegedOperationTransport helper) =>
            CoreModulePrivilegeBroker.Respond(async () =>
            {
                if (!state.IsSessionValid(http.User)) throw new ModulePrivilegeException("module.privilege_denied", 403);
                var result = await broker.Plans.ApproveAsync(id, CoreModulePrivilegeBroker.Session(http.User), approval,
                    () => VerifyAdministrator(http, approval, administrators, elevations, owners, state),
                    (operationId, operation) => CoreModulePrivilegeBroker.ExecuteCapabilityAsync(helper, operationId, operation));
                return Results.Ok(result);
            })).RequireAuthorization();
        return app;
    }
    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new ModulePrivilegeException("module.privilege_denied", 403);
    private static void EnsureConsentManagement(HttpContext http, ModuleManagementStore management, HostElevationSessionState state)
    {
        if (!management.Writable || !state.IsSessionValid(http.User)) throw new ModulePrivilegeException("module.privilege_denied", 403);
    }
    private static Task<ModulePrivilegeAdministrator?> VerifyAdministrator(HttpContext http, ModulePrivilegeApproval approval,
        IHostAdministratorAuthenticator administrators, IHostElevationSessionStore elevations, OwnerDeviceKeyService owners,
        HostElevationSessionState state)
    {
        if (owners.IsOwner(http.User)) return Task.FromResult<ModulePrivilegeAdministrator?>(new(
            "owner-device:" + http.User.FindFirstValue("device_id"), "windows-owner-device"));
        if (state.VerifiedAdministrator(http.User) is { AdministratorIdentity: { } identity } remembered)
            return Task.FromResult<ModulePrivilegeAdministrator?>(new(identity, remembered.AuthenticationMethod));
        var current = http.User.FindFirstValue("name") ?? http.User.Identity?.Name;
        if (string.IsNullOrEmpty(current)) return Task.FromResult<ModulePrivilegeAdministrator?>(null);
        var authentication = administrators.Authenticate(current, approval.AdministratorUsername, approval.Password);
        if (!authentication.Succeeded || string.IsNullOrEmpty(authentication.AdministratorIdentity)) return Task.FromResult<ModulePrivilegeAdministrator?>(null);
        elevations.Grant(http.User, HostElevationCapability.NativeServiceAction, "module-broker", false, authentication.AuthenticationMethod, http.TraceIdentifier);
        state.RecordAdministratorVerification(http.User, authentication);
        return Task.FromResult<ModulePrivilegeAdministrator?>(new(authentication.AdministratorIdentity, authentication.AuthenticationMethod));
    }
}
