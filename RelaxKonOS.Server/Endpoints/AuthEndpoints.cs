using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>认证 REST 端点。路由常量见 AuthApiRoutes。错误统一返回 RFC 7807 ProblemDetails，
/// 错误码通过 type URI 传递（ProblemDetails 无 Errors 字段，见 Protocol.md）。</summary>
public static class AuthEndpoints
{
    private const string ProblemBase = "https://relaxkonos.app/problems/";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").AddEndpointFilter<AuthenticationEndpointFilter>();
        group.MapPost(AuthApiRoutes.Login, async (
                LoginRequest req,
                HttpContext http,
                LoginAuthenticationService authentication,
                IUserRepository users,
                IWorkspaceRepository wss,
                IRegistryRepository registry,
                ISessionRepository sess,
                IDeviceRepository devs,
                JwtTokenService jwt,
                LoginProtectionService protection,
                CancellationToken ct) =>
            {
                var login = await authentication.AuthenticateAsync(req.Identifier, req.Password, http.Connection.RemoteIpAddress, ct);
                var user = login.User;
                var now = DateTimeOffset.UtcNow;

                // 查/建 Workspace（One User One Persistent，见 Workspace.md §4）
                var ws = wss.FindByUserId(user.Id)
                       ?? wss.Add(new Workspace
                       {
                           Id = Guid.NewGuid(),
                           UserId = user.Id,
                           Name = $"{user.Username} Workspace",
                           State = WorkspaceState.Running,
                           CreatedAt = now,
                       });

                // Configuration defaults are registry values. The legacy Workspace JSON columns
                // are intentionally not consulted or updated.
                WorkspaceConfigurationRegistry.EnsureDefaults(registry, ws, user.Id.ToString("D"));

                // 查/建 Device（按 name+platform 复用，更新版本与登录时间）
                var platformStr = req.ClientPlatform.ToString().ToLowerInvariant();
                var device = devs.FindByNameAndPlatform(req.DeviceName, platformStr);
                if (device is null)
                {
                    device = devs.Add(new Device
                    {
                        Id = Guid.NewGuid(),
                        Name = req.DeviceName,
                        Platform = platformStr,
                        ClientVersion = req.ClientVersion,
                    });
                }
                device.ClientVersion = req.ClientVersion;
                device.LastLoginAt = now;
                devs.Update(device);

                // 新建 Session（每次登录新建，Session ≠ Workspace）
                var session = sess.Add(new Session
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    AuthenticationMethod = login.Method,
                    AuthenticatedAt = now,
                    WorkspaceId = ws.Id,
                    DeviceId = device.Id,
                    CreatedAt = now,
                    LastActiveAt = now,
                    Status = SessionStatus.Active,
                });

                // 该设备成为 Controller（Grace Period 5 分钟，见 Workspace.md §19）
                ws.ControllerDeviceId = device.Id;
                ws.ControllerGrantedAt = now;
                ws.ControllerLeaseExpiresAt = now.AddMinutes(5);
                ws.State = WorkspaceState.Running;
                wss.Update(ws);

                users.UpdateLastLogin(user.Id, now);

                var role = DeviceRole.Controller;
                authentication.RequireCurrent(login);
                var tokens = jwt.Issue(user, ws, device, role, session.Id, login.Method, now, login.SecurityVersion);
                await protection.RecordSuccessAsync(login.ProtectionKey, http.Connection.RemoteIpAddress, ct, user.Id);

                return Results.Ok(new LoginResponse(
                    user.ToDto(), ws.ToDto(), session.ToDto(), device.ToDto(), tokens, role, CreateServerDescriptor()));
            })
            .RequireRateLimiting("login")
            .WithTags("Auth");

        group.MapPost(AuthApiRoutes.Refresh, (
                RefreshTokenRequest req,
                HttpContext http,
                AuthSessionStore sessions,
                IUserRepository users,
                IWorkspaceRepository wss,
                IDeviceRepository devs,
                JwtTokenService jwt,
                CanonicalUserResolver resolver,
                SessionValidityService validity) =>
            {
                if (string.IsNullOrEmpty(req.RefreshToken) || !sessions.TryConsume(req.RefreshToken, out var rec))
                    return Problem(http, 401, "invalid-credential", "Invalid credentials", "The refresh token is invalid, expired, or has already been used.");

                var user = users.FindById(rec.UserId);
                var ws = wss.FindById(rec.WorkspaceId);
                var device = devs.FindById(rec.DeviceId);
                if (user is null || ws is null || device is null || !validity.IsValid(rec.UserId, rec.SecurityVersion))
                    return Problem(http, 401, "invalid-credential", "Invalid credentials", "The session context is no longer valid.");

                var role = ws.ControllerDeviceId == device.Id ? DeviceRole.Controller : DeviceRole.Observer;
                resolver.RequireBinding(user, rec.AuthenticationMethod == "alias");
                if (!validity.IsValid(rec.UserId, rec.SecurityVersion)) return Results.Unauthorized();
                var tokens = jwt.Issue(user, ws, device, role, rec.SessionId, rec.AuthenticationMethod, rec.AuthenticatedAt, rec.SecurityVersion, rec.AbsoluteExpiresAt);
                return Results.Ok(new RefreshTokenResponse(tokens));
            })
            .WithTags("Auth");

        group.MapPost(AuthApiRoutes.Logout, (LogoutRequest? req, HttpContext http, AuthSessionStore sessions, IHostElevationSessionStore elevations) =>
            {
                if (!string.IsNullOrEmpty(req?.RefreshToken))
                    sessions.Revoke(req.RefreshToken);
                elevations.Revoke(http.User);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithTags("Auth");

        group.MapGet(AuthApiRoutes.Me, (ClaimsPrincipal principal, IUserRepository users, HttpContext http) =>
            {
                var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                          ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var userId))
                    return Problem(http, 401, "invalid-credential", "Invalid credentials", "The token is missing a user identity.");

                var user = users.FindById(userId);
                if (user is null)
                    return Results.NotFound();

                return Results.Ok(user.ToDto());
            })
            .RequireAuthorization()
            .WithTags("Auth");

        return app;
    }

    private static ServerDescriptorDto CreateServerDescriptor()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var capabilities = new List<string>
        {
            ServerCapabilities.Files,
            ServerCapabilities.Metrics,
            ServerCapabilities.Processes,
            ServerCapabilities.Terminal,
            ServerCapabilities.Git,
        };
        if (!isWindows)
        {
            capabilities.Add(ServerCapabilities.PosixPermissions);
            capabilities.Add(ServerCapabilities.Firewall);
        }

        return new ServerDescriptorDto(isWindows ? PlatformKind.Windows : PlatformKind.Linux, capabilities);
    }

    private static IResult Problem(HttpContext http, int status, string typeSuffix, string title, string detail)
        => Results.Problem(detail: detail, statusCode: status,
            title: ApiLocalizer.Get(http, typeSuffix, title), type: ProblemBase + typeSuffix);

    private static IResult TooManyAttempts(HttpContext http, DateTimeOffset retryAt)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(http, StatusCodes.Status429TooManyRequests, "login-rate-limited", "Too many login attempts",
            "Login attempts are temporarily limited. Try again later.");
    }
}
