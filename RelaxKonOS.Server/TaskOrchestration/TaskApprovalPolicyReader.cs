using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed class TaskApprovalException(string code, int statusCode = 403) : Exception(code)
{
    public string ProblemCode { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record TaskApprovalObservation(TaskExecutionIdentity Identity, PlatformUserInfo Requester,
    HostAccountPrivilege RequesterPrivilege, DateTimeOffset ValidUntil, RuntimeInstallationSnapshot Installation);
public interface ITaskApprovalPolicyReader
{
    Task<TaskApprovalObservation> ReadAsync(ClaimsPrincipal principal, CancellationToken ct);
}
public interface ITaskRuntimeHostObserver
{
    Task<RuntimeInstallationSnapshot> ReadAsync(CancellationToken ct);
}
public sealed class SingBoxTaskRuntimeHostObserver(SingBoxRuntimeService runtime) : ITaskRuntimeHostObserver
{
    public Task<RuntimeInstallationSnapshot> ReadAsync(CancellationToken ct) => runtime.QueryAsync(ct);
}

/// <summary>Reads current repository/OS policy in fresh scopes; neither JWT role nor cached elevation is sufficient.</summary>
public sealed class TaskApprovalPolicyReader(IServiceScopeFactory scopes, ITaskRuntimeHostObserver host,
    TimeProvider? time = null) : ITaskApprovalPolicyReader
{
    private readonly TimeProvider _clock = time ?? TimeProvider.System;
    public async Task<TaskApprovalObservation> ReadAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        try
        {
            var before = ReadLocal(principal);
            var installation = await host.ReadAsync(ct);
            if (installation.Installation is not { RuntimeId: "sing-box" } runtime || string.IsNullOrWhiteSpace(runtime.HostId))
                throw new TaskApprovalException("task.observation_unavailable", 503);
            var after = ReadLocal(principal);
            if (before.Revision != after.Revision) throw new TaskApprovalException("task.policy_changed", 409);
            return new(new(after.Subject, after.Session, runtime.HostId, after.Revision), after.Requester,
                after.Privilege, before.ValidUntil < after.ValidUntil ? before.ValidUntil : after.ValidUntil, installation);
        }
        catch (AliasAuthenticationException ex) { throw new TaskApprovalException("task.identity_unavailable", ex.Status == 503 ? 503 : 403); }
        catch (RuntimeLifecycleException) { throw new TaskApprovalException("task.observation_unavailable", 503); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or DbException or IOException)
        { throw new TaskApprovalException("task.identity_unavailable", 503); }
    }

    private LocalPolicy ReadLocal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true
            || principal.HasClaim(RelaxKonOSAuthSchemes.TokenTypeClaim, RelaxKonOSAuthSchemes.FileCapabilityTokenType)
            || !InstallationEndpoints.CanInstall(principal, InstallationServiceId.SingBox))
            throw new TaskApprovalException("task.permission_denied");
        var subject = Claim(principal, "sub", ClaimTypes.NameIdentifier);
        var sid = Claim(principal, "sid");
        if (!Guid.TryParseExact(subject, "D", out var userId) || !Guid.TryParseExact(sid, "D", out var sessionId)
            || !long.TryParse(Claim(principal, "security_version"), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !long.TryParse(Claim(principal, "auth_time"), NumberStyles.None, CultureInfo.InvariantCulture, out var authenticatedAt)
            || !long.TryParse(Claim(principal, "exp"), NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || version < 0 || expires <= _clock.GetUtcNow().ToUnixTimeSeconds()
            || expires > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) throw new TaskApprovalException("task.session_unavailable");
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var mode = services.GetRequiredService<IServerModeResolver>();
        if (mode.Mode != ServerMode.System || !mode.Supports(ServerHostFeature.PrivilegedOperations)
            || !mode.Supports(ServerHostFeature.AgentInstallation)) throw new TaskApprovalException("task.permission_denied");
        var user = services.GetRequiredService<IUserRepository>().FindById(userId);
        var session = services.GetRequiredService<ISessionRepository>().FindById(sessionId);
        if (user is not { IdentityReviewRequired: false } || user.SecurityVersion != version
            || session is not { Status: SessionStatus.Active } || session.UserId != userId
            || session.AuthenticationMethod != Claim(principal, "amr") || session.AuthenticatedAt.ToUnixTimeSeconds() != authenticatedAt
            || session.WorkspaceId.ToString("D") != Claim(principal, "workspace_id")
            || session.DeviceId.ToString("D") != Claim(principal, "device_id")
            || !services.GetRequiredService<SessionValidityService>().IsValid(principal))
            throw new TaskApprovalException("task.session_unavailable");
        var workspace = services.GetRequiredService<IWorkspaceRepository>().FindById(session.WorkspaceId);
        var device = services.GetRequiredService<IDeviceRepository>().FindById(session.DeviceId);
        var now = _clock.GetUtcNow();
        if (workspace is null || workspace.UserId != userId || workspace.State != WorkspaceState.Running || device is null
            || workspace.ControllerDeviceId != session.DeviceId || workspace.ControllerGrantedAt is null
            || workspace.ControllerLeaseExpiresAt is not { } leaseUntil || leaseUntil <= now)
            throw new TaskApprovalException("task.permission_denied");
        var requester = services.GetRequiredService<CanonicalUserResolver>().RequireBinding(user, requireEligibility: session.AuthenticationMethod == "alias");
        var privilege = services.GetRequiredService<IHostAccountPrivilegeService>().Classify(principal);
        if (!Enum.IsDefined(privilege)) throw new TaskApprovalException("task.identity_unavailable", 503);
        var revision = Convert.ToHexStringLower(SHA256.HashData(TaskPlanCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            schema = "task-sing-box-policy-1", userId, user.Platform, user.PlatformIdentity, version, sessionId,
            session.WorkspaceId, session.DeviceId, session.AuthenticationMethod, authenticatedAt,
            workspace.ControllerDeviceId, workspace.ControllerGrantedAt, privilege, mode.Mode,
            installationRestrictions = principal.FindAll("installation:install").Select(item => item.Value)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        }))));
        var tokenUntil = DateTimeOffset.FromUnixTimeSeconds(expires);
        return new(userId.ToString("D"), sessionId.ToString("D"), revision, requester, privilege,
            tokenUntil < leaseUntil ? tokenUntil : leaseUntil);
    }

    private static string Claim(ClaimsPrincipal principal, string type, string? alternate = null)
    {
        var values = principal.Claims.Where(item => item.Type == type || alternate is not null && item.Type == alternate)
            .Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : throw new TaskApprovalException("task.session_unavailable");
    }
    private sealed record LocalPolicy(string Subject, string Session, string Revision, PlatformUserInfo Requester,
        HostAccountPrivilege Privilege, DateTimeOffset ValidUntil);
}
