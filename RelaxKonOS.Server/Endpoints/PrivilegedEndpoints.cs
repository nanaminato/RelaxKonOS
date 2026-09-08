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
            IHostAdministratorAuthenticator administrators, IHostElevationSessionStore elevations) =>
        {
            if (!Enum.IsDefined(request.Capability)) return Problem(400, "elevation-capability-invalid", "授权能力无效。");
            if (request.Capability is >= HostElevationCapability.FileRead and <= HostElevationCapability.FileUpload)
                return Problem(400, "file-elevation-capability-invalid", "文件操作必须使用文件授权入口。");
            var username = http.User.FindFirstValue(JwtRegisteredClaimNames.Name);
            if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
            var authentication = administrators.Authenticate(username, request.AdministratorUsername, request.Password);
            if (!authentication.Succeeded) return Problem(403, authentication.ProblemCode, "宿主管理员认证未通过，未执行操作。");
            try
            {
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
