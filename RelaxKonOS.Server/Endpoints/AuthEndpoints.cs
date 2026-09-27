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
using RelaxKonOS.Server.HostMode;

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
                IServerModeResolver serverMode,
                WindowsDesktopSessionOptions desktopSession,
                CancellationToken ct) =>
            {
                if (desktopSession.Enabled)
                    return Problem(http, 403, "windows-desktop-session-required", "Windows Desktop session required",
                        "This loopback development Server accepts only the current Windows session.");
                var login = await authentication.AuthenticateAsync(req.Identifier, req.Password, http.Connection.RemoteIpAddress, ct);
                return await CompleteLoginAsync(login, req.ClientPlatform, req.DeviceName, req.ClientVersion, http,
                    authentication, users, wss, registry, sess, devs, jwt, protection, serverMode, ct);
            })
            .RequireRateLimiting("login")
            .WithTags("Auth");

        group.MapPost(AuthApiRoutes.WindowsDesktopSession, async (
                WindowsDesktopSessionLoginRequest req,
                HttpContext http,
                LoginAuthenticationService authentication,
                IUserRepository users,
                IWorkspaceRepository wss,
                IRegistryRepository registry,
                ISessionRepository sess,
                IDeviceRepository devs,
                JwtTokenService jwt,
                LoginProtectionService protection,
                IServerModeResolver serverMode,
                CancellationToken ct) =>
            {
                var login = authentication.AuthenticateWindowsDesktopSession(http.User);
                return await CompleteLoginAsync(login, req.ClientPlatform, req.DeviceName, req.ClientVersion, http,
                    authentication, users, wss, registry, sess, devs, jwt, protection, serverMode, ct);
            })
            .RequireAuthorization("WindowsDesktopSessionLogin")
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

        group.MapGet(ServerApiRoutes.Capabilities, (IServerModeResolver serverMode) => Results.Ok(serverMode.Describe()))
            .RequireAuthorization()
            .WithTags("Server");

        group.MapPost(OwnerDeviceKeyApiRoutes.LocalBootstrap, async (OwnerDeviceBootstrapRequest request, HttpContext http,
                LoginAuthenticationService authentication, OwnerDeviceKeyService ownerDevices,
                IUserRepository users, IWorkspaceRepository workspaces, IRegistryRepository registry, ISessionRepository sessions,
                IDeviceRepository devices, JwtTokenService jwt, LoginProtectionService protection, IServerModeResolver serverMode,
                CancellationToken ct) =>
            {
                if (http.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))
                    throw new AliasAuthenticationException(403, "owner-device-loopback-required");
                if (!ownerDevices.IsAvailable)
                    throw new OwnerDeviceKeyException(404, "owner-device-unsupported-platform");
                var login = authentication.AuthenticateWindowsWorkstationOwnerBootstrap(http.User);
                var platform = request.Platform.Trim().ToLowerInvariant();
                var device = devices.FindByNameAndPlatform(request.DeviceName.Trim(), platform) ?? devices.Add(new Device
                {
                    Id = Guid.NewGuid(), Name = request.DeviceName.Trim(), Platform = platform,
                    ClientVersion = request.ClientVersion.Trim(),
                });
                ownerDevices.RegisterOrReplaceLocalWindowsDevice(login.User.Id, device.Id, request);
                return await CompleteLoginAsync(login, OwnerClientPlatform(device.Platform), device.Name, device.ClientVersion, http,
                    authentication, users, workspaces, registry, sessions, devices, jwt, protection, serverMode, ct, device);
            })
            .RequireAuthorization("WindowsOwnerDeviceBootstrap")
            .WithTags("Owner devices");

        group.MapPost(OwnerDeviceKeyApiRoutes.Bootstrap, (OwnerDeviceBootstrapRequest request, ClaimsPrincipal principal,
                IOwnerDeviceKeyRepository keys, OwnerDeviceKeyService ownerDevices) =>
            {
                var userId = Subject(principal);
                var deviceId = DeviceId(principal);
                if (keys.ListActive(userId).Count != 0)
                    return Results.Conflict(new { problemCode = "owner-device-bootstrap-complete" });
                var key = ownerDevices.Register(userId, deviceId, request);
                return Results.Created(OwnerDeviceKeyApiRoutes.Device(key.Id), ToOwnerDeviceDto(key, key.Id));
            })
            .RequireAuthorization()
            .WithTags("Owner devices");

        group.MapPost(OwnerDeviceKeyApiRoutes.Challenge, (OwnerDeviceChallengeRequest request, OwnerDeviceKeyService ownerDevices) =>
            Results.Ok(ownerDevices.CreateChallenge(request.DeviceId)))
            .RequireRateLimiting("login")
            .WithTags("Owner devices");

        group.MapPost(OwnerDeviceKeyApiRoutes.SignIn, async (OwnerDeviceSignInRequest request, HttpContext http,
                OwnerDeviceKeyService ownerDevices, IUserRepository users, IWorkspaceRepository workspaces,
                IRegistryRepository registry, ISessionRepository sessions, IDeviceRepository devices, JwtTokenService jwt,
                LoginProtectionService protection, LoginAuthenticationService authentication, IServerModeResolver serverMode,
                CancellationToken ct) =>
            {
                var key = ownerDevices.VerifyChallenge(request.ChallengeId, request.DeviceId, request.Signature);
                var user = users.FindById(key.UserId) ?? throw new OwnerDeviceKeyException(401, "owner-device-user-unavailable");
                var device = devices.FindById(key.DeviceId) ?? throw new OwnerDeviceKeyException(401, "owner-device-unavailable");
                var login = new AuthenticatedLogin(user, "owner-device-key", 0, user.SecurityVersion, user.Id.ToString("D"),
                    RelaxKonOS.Server.UserExecution.UserExecutionEligibilityRules.Evaluate(
                        new PlatformUserInfo(user.PlatformIdentity ?? string.Empty, user.Username, user.Platform, user.Username, null), serverMode.Mode));
                return await CompleteLoginAsync(login, OwnerClientPlatform(device.Platform), device.Name, device.ClientVersion, http,
                    authentication, users, workspaces, registry, sessions, devices, jwt, protection, serverMode, ct, device);
            })
            .RequireRateLimiting("login")
            .WithTags("Owner devices");

        group.MapPost(OwnerDeviceKeyApiRoutes.Invitations, (ClaimsPrincipal principal, OwnerDeviceKeyService ownerDevices) =>
            Results.Ok(ownerDevices.CreateInvitation(principal)))
            .RequireAuthorization()
            .WithTags("Owner devices");

        group.MapPost(OwnerDeviceKeyApiRoutes.AcceptInvitation, (OwnerDeviceAcceptInvitationRequest request,
                IDeviceRepository devices, OwnerDeviceKeyService ownerDevices) =>
            {
                if (!ownerDevices.IsAvailable)
                    throw new OwnerDeviceKeyException(404, "owner-device-unsupported-platform");
                ownerDevices.EnsureInvitationIsUsable(request.Token);
                var platform = request.Platform.Trim().ToLowerInvariant();
                if (devices.FindByNameAndPlatform(request.DeviceName.Trim(), platform) is not null)
                    return Results.Conflict(new { problemCode = "owner-device-name-in-use" });
                var device = devices.Add(new Device
                {
                    Id = Guid.NewGuid(), Name = request.DeviceName.Trim(), Platform = platform,
                    ClientVersion = request.ClientVersion.Trim(), LastLoginAt = null,
                });
                var key = ownerDevices.AcceptInvitation(request, device.Id);
                return Results.Created(OwnerDeviceKeyApiRoutes.Device(key.Id), ToOwnerDeviceDto(key, key.Id));
            })
            .RequireRateLimiting("login")
            .WithTags("Owner devices");

        group.MapGet(OwnerDeviceKeyApiRoutes.Devices, (ClaimsPrincipal principal, OwnerDeviceKeyService ownerDevices) =>
            Results.Ok(ownerDevices.List(principal).Select(key => ToOwnerDeviceDto(key, DeviceId(principal)))))
            .RequireAuthorization()
            .WithTags("Owner devices");

        group.MapDelete(OwnerDeviceKeyApiRoutes.DeviceTemplate, (Guid id, ClaimsPrincipal principal, OwnerDeviceKeyService ownerDevices) =>
            {
                ownerDevices.Revoke(principal, id);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithTags("Owner devices");

        return app;
    }

    private static async Task<IResult> CompleteLoginAsync(AuthenticatedLogin login, ClientPlatformKind clientPlatform,
        string deviceName, string clientVersion, HttpContext http, LoginAuthenticationService authentication,
        IUserRepository users, IWorkspaceRepository wss, IRegistryRepository registry, ISessionRepository sess,
        IDeviceRepository devs, JwtTokenService jwt, LoginProtectionService protection, IServerModeResolver serverMode,
        CancellationToken ct, Device? existingDevice = null)
    {
        var user = login.User;
        var now = DateTimeOffset.UtcNow;
        var ws = wss.FindByUserId(user.Id) ?? wss.Add(new Workspace
        {
            Id = Guid.NewGuid(), UserId = user.Id, Name = $"{user.Username} Workspace", State = WorkspaceState.Running, CreatedAt = now,
        });
        WorkspaceConfigurationRegistry.EnsureDefaults(registry, ws, user.Id.ToString("D"));
        var platform = clientPlatform.ToString().ToLowerInvariant();
        var device = existingDevice ?? devs.FindByNameAndPlatform(deviceName, platform) ?? devs.Add(new Device
        {
            Id = Guid.NewGuid(), Name = deviceName, Platform = platform, ClientVersion = clientVersion,
        });
        device.ClientVersion = clientVersion;
        device.LastLoginAt = now;
        devs.Update(device);
        var session = sess.Add(new Session
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthenticationMethod = login.Method, AuthenticatedAt = now,
            WorkspaceId = ws.Id, DeviceId = device.Id, CreatedAt = now, LastActiveAt = now, Status = SessionStatus.Active,
        });
        ws.ControllerDeviceId = device.Id;
        ws.ControllerGrantedAt = now;
        ws.ControllerLeaseExpiresAt = now.AddMinutes(5);
        ws.State = WorkspaceState.Running;
        wss.Update(ws);
        users.UpdateLastLogin(user.Id, now);
        authentication.RequireCurrent(login);
        var role = DeviceRole.Controller;
        var tokens = jwt.Issue(user, ws, device, role, session.Id, login.Method, now, login.SecurityVersion);
        await protection.RecordSuccessAsync(login.ProtectionKey, http.Connection.RemoteIpAddress, ct, user.Id);
        return Results.Ok(new LoginResponse(user.ToDto(), ws.ToDto(), session.ToDto(), device.ToDto(), tokens, role,
            CreateServerDescriptor(serverMode), new ServerExecutionEligibilityDto(login.ExecutionEligibility.Available,
                login.ExecutionEligibility.ReasonCode, serverMode.Mode == ServerMode.System && login.Method == "system"
                && user.Platform == HostPlatformKind.Linux && user.PlatformIdentity == "0" && user.Username == "root")));
    }

    private static ServerDescriptorDto CreateServerDescriptor(IServerModeResolver serverMode)
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
        if (!isWindows && serverMode.Supports(ServerHostFeature.Firewall))
        {
            capabilities.Add(ServerCapabilities.PosixPermissions);
            capabilities.Add(ServerCapabilities.Firewall);
        }

        var host = serverMode.Describe();
        if (host.Capabilities.Guardian) capabilities.Add(ServerCapabilities.Guardian);
        if (host.Capabilities.Docker) capabilities.Add(ServerCapabilities.Docker);
        if (host.Capabilities.FileServices) capabilities.Add(ServerCapabilities.FileServices);
        if (host.Capabilities.WebServer) capabilities.Add(ServerCapabilities.WebServer);
        if (host.Capabilities.Certificates) capabilities.Add(ServerCapabilities.Certificates);
        if (host.Capabilities.Tunnels) capabilities.Add(ServerCapabilities.Tunnels);
        if (host.Capabilities.Proxy) capabilities.Add(ServerCapabilities.Proxy);
        if (host.Capabilities.ApplicationDeployments) capabilities.Add(ServerCapabilities.ApplicationDeployments);
        return new ServerDescriptorDto(isWindows ? HostPlatformKind.Windows : HostPlatformKind.Linux, capabilities, host);
    }

    private static IResult Problem(HttpContext http, int status, string typeSuffix, string title, string detail)
        => Results.Problem(detail: detail, statusCode: status,
            title: ApiLocalizer.Get(http, typeSuffix, title), type: ProblemBase + typeSuffix);

    private static Guid Subject(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new OwnerDeviceKeyException(401, "owner-device-user-unavailable");
        return userId;
    }

    private static Guid DeviceId(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirstValue("device_id"), out var deviceId))
            throw new OwnerDeviceKeyException(401, "owner-device-unavailable");
        return deviceId;
    }

    private static OwnerDeviceDto ToOwnerDeviceDto(OwnerDeviceKey key, Guid currentDeviceId) => new(
        key.Id, key.Name, key.Platform, key.CreatedAt, key.LastUsedAt, key.Id == currentDeviceId);

    private static ClientPlatformKind OwnerClientPlatform(string platform) => platform.ToLowerInvariant() switch
    {
        "linux" => ClientPlatformKind.Linux,
        "android" => ClientPlatformKind.Android,
        "ios" => ClientPlatformKind.iOS,
        _ => ClientPlatformKind.Windows,
    };

    private static IResult TooManyAttempts(HttpContext http, DateTimeOffset retryAt)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(http, StatusCodes.Status429TooManyRequests, "login-rate-limited", "Too many login attempts",
            "Login attempts are temporarily limited. Try again later.");
    }
}
