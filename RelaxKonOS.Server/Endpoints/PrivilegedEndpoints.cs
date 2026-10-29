using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Identity;
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
            RelaxKonOS.Server.Settings.IHostEnvironmentService environment, OwnerDeviceKeyService ownerDevices,
            RelaxKonOS.Server.HostMode.IServerModeResolver mode) =>
        {
            if (!mode.Supports(RelaxKonOS.Server.HostMode.ServerHostFeature.PrivilegedOperations))
                return Problem(403, "privileged-feature-unavailable", "This deployment does not offer privileged operations.");
            if (!Enum.IsDefined(request.Capability)) return Problem(400, "elevation-capability-invalid", "The authorization capability is not valid.");
            if (request.Capability is >= HostElevationCapability.FileRead and <= HostElevationCapability.FileUpload)
                return Problem(400, "file-elevation-capability-invalid", "File operations must use the file authorization entry point.");
            if (string.IsNullOrWhiteSpace(request.Target) || request.Target.Length > 256 || request.IncludeDescendants)
                return Problem(400, "elevation-target-invalid", "The target resource is not valid.");
            if (request.Capability == HostElevationCapability.ProcessManage && request.Target != "host/processes")
                return Problem(400, "elevation-target-invalid", "Process-management authorization must target host/processes.");
            if (request.Capability == HostElevationCapability.RegistryManage && request.Target != "registry/users"
                && (!request.Target.StartsWith("registry/users/", StringComparison.Ordinal)
                    || !Guid.TryParseExact(request.Target["registry/users/".Length..], "D", out _)))
                return Problem(400, "elevation-target-invalid", "Registry authorization must name a user.");
            if (request.Capability == HostElevationCapability.ServerRuntimeConfigure && request.Target != RelaxKonOS.Protocol.Registry.ServerRuntimeRoutes.ElevationTarget)
                return Problem(400, "elevation-target-invalid", "Invalid server runtime target.");
            var isEnvironmentCapability = request.Capability is HostElevationCapability.HostEnvironmentRead or HostElevationCapability.HostEnvironmentChange;
            if (isEnvironmentCapability)
            {
                try
                {
                    var scope = request.Target == "host/environment/machine" ? RelaxKonOS.Protocol.Settings.SettingsScope.HostMachine : RelaxKonOS.Protocol.Settings.SettingsScope.HostUser;
                    if (environment.ResolveTarget(http.User, scope).ResourceId != request.Target)
                        return Problem(403, "environment-target-denied", "The environment target does not belong to the authenticated identity.");
                    if (OperatingSystem.IsWindows() && scope == RelaxKonOS.Protocol.Settings.SettingsScope.HostUser)
                        return Results.Ok(new HostElevationResult(true));
                }
                catch (RelaxKonOS.Server.Settings.SettingsException error) { return Problem(error.StatusCode, error.Code, "The environment identity could not be resolved."); }
            }
            // Current administrators are revalidated here; no automatic grant is cached.
            if (elevations.IsGranted(http.User, request.Capability, request.Target))
                return Results.Ok(new HostElevationResult(true));
            var authentication = ownerDevices.IsOwner(http.User)
                ? new HostAdministratorAuthenticationResult(true, string.Empty, "windows-owner-device", "owner-device:" + http.User.FindFirstValue("device_id"))
                : AuthenticateAdministrator(http.User, request, administrators);
            if (!authentication.Succeeded) return Problem(403, authentication.ProblemCode, "Host administrator authentication failed; nothing was executed.");
            try
            {
                // A manual authorization covers only the requested store. Read and change
                // expire together; authorizing the user's store must not authorize the machine.
                if (isEnvironmentCapability)
                {
                    var scope = request.Target == "host/environment/machine"
                        ? RelaxKonOS.Protocol.Settings.SettingsScope.HostMachine : RelaxKonOS.Protocol.Settings.SettingsScope.HostUser;
                    var target = environment.ResolveTarget(http.User, scope);
                    DateTimeOffset environmentExpires = default;
                    foreach (var capability in new[]
                    {
                        HostElevationCapability.HostEnvironmentRead,
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
            catch (ArgumentException) { return Problem(400, "elevation-target-invalid", "The target resource is not valid."); }
        }).RequireAuthorization().WithTags("Privileged Operations");
        return app;
    }

    private static IResult Problem(int status, string code, string detail) => Results.Problem(detail: detail, statusCode: status,
        title: code, type: ProblemBase + code,
        extensions: new Dictionary<string, object?> { ["problemCode"] = code });

    private static HostAdministratorAuthenticationResult AuthenticateAdministrator(ClaimsPrincipal principal,
        HostElevationRequest request, IHostAdministratorAuthenticator administrators)
    {
        var username = principal.FindFirstValue(JwtRegisteredClaimNames.Name);
        return string.IsNullOrWhiteSpace(username)
            ? new(false, "elevation-session-unavailable", "none", null)
            : administrators.Authenticate(username, request.AdministratorUsername, request.Password);
    }
}
