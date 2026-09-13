using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Generic elevation grant endpoint for non-file, exact-resource capabilities.</summary>
public static class PrivilegedEndpoints
{
    private const string ProblemBase = "https://relaxkonos.app/problems/";

    public static IEndpointRouteBuilder MapPrivilegedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(PrivilegedApiRoutes.Elevation, (HostElevationRequest request, HttpContext http,
            IHostAdministratorAuthenticator administrators, IHostElevationSessionStore elevations,
            RelaxKonOS.Server.Settings.IHostEnvironmentService environment) =>
        {
            if (!Enum.IsDefined(request.Capability)) return Problem(400, "elevation-capability-invalid", "授权能力无效。");
            if (request.Capability is >= HostElevationCapability.FileRead and <= HostElevationCapability.FileUpload)
                return Problem(400, "file-elevation-capability-invalid", "文件操作必须使用文件授权入口。");
            if (string.IsNullOrWhiteSpace(request.Target) || request.Target.Length > 256 || request.IncludeDescendants)
                return Problem(400, "elevation-target-invalid", "目标资源无效。");
            var isEnvironmentCapability = request.Capability is HostElevationCapability.HostEnvironmentRead or HostElevationCapability.HostEnvironmentChange or HostElevationCapability.HostEnvironmentReveal;
            if (isEnvironmentCapability)
            {
                try
                {
                    var scope = request.Target == "host/environment/machine" ? RelaxKonOS.Protocol.Settings.SettingsScope.HostMachine : RelaxKonOS.Protocol.Settings.SettingsScope.HostUser;
                    if (environment.ResolveTarget(http.User, scope).ResourceId != request.Target)
                        return Problem(403, "environment-target-denied", "环境目标不属于当前认证身份。");
                }
                catch (RelaxKonOS.Server.Settings.SettingsException error) { return Problem(error.StatusCode, error.Code, "环境身份映射失败。"); }
            }
            // An older single-capability environment grant is deliberately upgraded on the
            // next request; only a complete three-capability grant for both stores can skip
            // verification.
            var environmentBundleAlreadyGranted = isEnvironmentCapability
                && new[] { RelaxKonOS.Protocol.Settings.SettingsScope.HostUser, RelaxKonOS.Protocol.Settings.SettingsScope.HostMachine }
                    .All(scope => new[]
                    {
                        HostElevationCapability.HostEnvironmentRead,
                        HostElevationCapability.HostEnvironmentReveal,
                        HostElevationCapability.HostEnvironmentChange,
                    }.All(capability => elevations.IsGranted(http.User, capability, environment.ResolveTarget(http.User, scope).ResourceId)));
            if (environmentBundleAlreadyGranted || !isEnvironmentCapability && elevations.IsGranted(http.User, request.Capability, request.Target))
                return Results.Ok(new HostElevationResult(true));
            var username = http.User.FindFirstValue(JwtRegisteredClaimNames.Name);
            if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
            var authentication = administrators.Authenticate(username, request.AdministratorUsername, request.Password);
            if (!authentication.Succeeded) return Problem(403, authentication.ProblemCode, "宿主管理员认证未通过，未执行操作。");
            try
            {
                // Environment variables are one Windows control-panel action.  A successful
                // administrator verification grants the read, reveal, and change capabilities
                // for both stores owned by this authenticated user, rather than prompting once
                // to open the dialog and again for every edit.  All grants remain token-bound
                // and expire together after the normal short session lifetime.
                if (isEnvironmentCapability)
                {
                    var targets = new[]
                    {
                        environment.ResolveTarget(http.User, RelaxKonOS.Protocol.Settings.SettingsScope.HostUser),
                        environment.ResolveTarget(http.User, RelaxKonOS.Protocol.Settings.SettingsScope.HostMachine),
                    };
                    DateTimeOffset environmentExpires = default;
                    foreach (var target in targets)
                    foreach (var capability in new[]
                    {
                        HostElevationCapability.HostEnvironmentRead,
                        HostElevationCapability.HostEnvironmentReveal,
                        HostElevationCapability.HostEnvironmentChange,
                    })
                        environmentExpires = elevations.Grant(http.User, capability, target.ResourceId, includeDescendants: false,
                            authentication.AuthenticationMethod, http.TraceIdentifier);
                    return Results.Ok(new HostElevationResult(true, environmentExpires));
                }
                var expires = elevations.Grant(http.User, request.Capability, request.Target, request.IncludeDescendants,
                    authentication.AuthenticationMethod, http.TraceIdentifier);
                return Results.Ok(new HostElevationResult(true, expires));
            }
            catch (ArgumentException) { return Problem(400, "elevation-target-invalid", "目标资源无效。"); }
        }).RequireAuthorization().WithTags("Privileged Operations");
        return app;
    }

    private static IResult Problem(int status, string code, string detail) => Results.Problem(detail: detail, statusCode: status,
        title: "需要管理员权限", type: ProblemBase + code);
}
