using System.Security.Claims;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// One route pair for every component the host registers. The path names the component; which
/// product answers, and whether it is a ledgered archive or a distribution package, is decided by
/// the adapter registry and never by the caller.
/// </summary>
public static class ComponentEndpoints
{
    public static IEndpointRouteBuilder MapComponentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireHostFeature(ServerHostFeature.PrivilegedOperations).WithTags("Components");
        group.MapGet(ComponentRoutes.ReleasesPattern, async (string componentId, bool? refresh, IRuntimeDownloadProxyProvider downloadProxy, CancellationToken ct) =>
        {
            try
            {
                var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => "x64",
                    System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                    _ => null
                };
                if (architecture is null || !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                    return Problem(ComponentProblemCodes.PlatformUnsupported,409);
                var rid = (OperatingSystem.IsWindows() ? "win-" : "linux-") + architecture;
                var proxy = componentId == ComponentIds.Tailscale ? await downloadProxy.ResolveAsync(ct) : null;
                return Results.Ok(await new RelaxKonOS.ManagedRuntimes.ComponentPackages.OfficialComponentReleases().ListAsync(componentId, rid, refresh == true, ct, proxy));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
            { return Problem(ComponentProblemCodes.PackageIndexUnavailable, 503); }
            catch (RelaxKonOS.ManagedRuntimes.RuntimePolicyException ex)
            { return ex.ProblemCode.StartsWith("runtime.", StringComparison.Ordinal) ? Problem(ComponentProblemCodes.PackageIndexUnavailable,503) : Problem(ex.ProblemCode,400); }
            catch (RuntimeLifecycleException ex) { return Problem(ex.ProblemCode,503); }
        }).RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy);
        group.MapGet(ComponentRoutes.StatePattern, async (string componentId, ComponentLifecycleRegistry registry, CancellationToken ct) =>
            await RespondAsync(async () => Results.Ok(await registry.Require(componentId).StateAsync(ct))))
            .RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy);
        group.MapPost(ComponentRoutes.PlanPattern, async (string componentId, ComponentPlanRequest request, HttpContext http,
            ComponentLifecycleRegistry registry, CancellationToken ct) =>
        {
            var adapter = registry.Require(componentId);
            // A request for another component must not be answered under this path.
            if (!string.Equals(request.ComponentId, componentId, StringComparison.Ordinal))
                return Problem(ComponentProblemCodes.RequestInvalid, 400);
            if (!CanManage(http.User, componentId)) return Results.Forbid();
            return await RespondAsync(async () => Results.Ok(await adapter.PlanAsync(request, ct)));
        }).RequireAuthorization(RuntimeCatalogEndpoints.ManagePolicy);
        return app;
    }

    /// <summary>Manage rights stay per product even though the route is shared.</summary>
    private static bool CanManage(ClaimsPrincipal user, string componentId) => componentId switch
    {
        ComponentIds.SingBox => InstallationEndpoints.CanInstall(user, InstallationServiceId.SingBox),
        ComponentIds.Mihomo => InstallationEndpoints.CanInstall(user, InstallationServiceId.Mihomo),
        ComponentIds.Nginx => InstallationEndpoints.CanInstall(user, InstallationServiceId.Nginx),
        ComponentIds.Squid => InstallationEndpoints.CanInstall(user, InstallationServiceId.Squid),
        ComponentIds.Samba => InstallationEndpoints.CanInstall(user, InstallationServiceId.Smb),
        ComponentIds.Docker => InstallationEndpoints.CanInstall(user, InstallationServiceId.Docker),
        ComponentIds.Tailscale => InstallationEndpoints.CanInstall(user, InstallationServiceId.Tailscale),
        ComponentIds.Frpc or ComponentIds.Frps => InstallationEndpoints.CanInstall(user, InstallationServiceId.Frp),
        _ => false
    };

    private static async Task<IResult> RespondAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ComponentLifecycleException ex)
        {
            return Problem(ex.ProblemCode, ex.ProblemCode switch
            {
                ComponentProblemCodes.UnknownComponent => 404,
                ComponentProblemCodes.RequestInvalid => 400,
                ComponentProblemCodes.PlatformUnsupported or ComponentProblemCodes.PackageIndexUnavailable => 409,
                _ => 503
            });
        }
        catch (RuntimeLifecycleException) { return Problem(ComponentProblemCodes.StateUnavailable, 503); }
        catch (RuntimeCatalogException) { return Problem(ComponentProblemCodes.StateUnavailable, 503); }
        catch (IOException) { return Problem(ComponentProblemCodes.StateUnavailable, 503); }
    }

    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
