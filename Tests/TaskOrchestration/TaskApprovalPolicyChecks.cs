using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskApprovalPolicyChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var clock = new Clock();
        var user = new User { Id = Guid.NewGuid(), Username = "alice", PlatformIdentity = "1000", Platform = HostPlatformKind.Linux };
        var device = new Device { Id = Guid.NewGuid() };
        var workspace = new Workspace { Id = Guid.NewGuid(), UserId = user.Id, State = WorkspaceState.Running,
            ControllerDeviceId = device.Id, ControllerGrantedAt = clock.Now, ControllerLeaseExpiresAt = clock.Now.AddMinutes(5) };
        var session = new Session { Id = Guid.NewGuid(), UserId = user.Id, DeviceId = device.Id,
            WorkspaceId = workspace.Id, Status = SessionStatus.Active, AuthenticationMethod = "system", AuthenticatedAt = clock.Now };
        var users = new InMemoryUserRepository(); users.Add(user);
        var devices = new InMemoryDeviceRepository(); devices.Add(device);
        var workspaces = new InMemoryWorkspaceRepository(); workspaces.Add(workspace);
        var sessions = new InMemorySessionRepository(); sessions.Add(session);
        var identities = new Identities(); var privilege = new Privilege(); var mode = new HostModeFixture(); var observer = new Observer();
        var services = new ServiceCollection();
        services.AddSingleton<IUserRepository>(users).AddSingleton<IDeviceRepository>(devices)
            .AddSingleton<IWorkspaceRepository>(workspaces).AddSingleton<ISessionRepository>(sessions)
            .AddSingleton<IAliasCredentialRepository, InMemoryAliasCredentialRepository>()
            .AddSingleton<AuthSessionStore>().AddSingleton<SessionValidityService>()
            .AddScoped<CanonicalUserResolver>().AddSingleton<IIdentityProvider>(identities)
            .AddSingleton<IHostAccountPrivilegeService>(privilege).AddSingleton<IServerModeResolver>(mode);
        using var provider = services.BuildServiceProvider();
        var reader = new TaskApprovalPolicyReader(provider.GetRequiredService<IServiceScopeFactory>(), observer, clock);
        ClaimsPrincipal Principal(string role = "controller", params Claim[] extra) => new(new ClaimsIdentity(new Claim[]
        {
            new("sub", user.Id.ToString("D")), new("sid", session.Id.ToString("D")), new("security_version", "0"),
            new("auth_time", session.AuthenticatedAt.ToUnixTimeSeconds().ToString()), new("amr", "system"),
            new("workspace_id", workspace.Id.ToString("D")), new("device_id", device.Id.ToString("D")),
            new("exp", clock.Now.AddMinutes(10).ToUnixTimeSeconds().ToString()), new("role", role)
        }.Concat(extra), "fixture"));
        async Task Reject(ClaimsPrincipal principal, string code, bool beforeHost = true)
        {
            var calls = observer.Calls;
            try { await reader.ReadAsync(principal, default); throw new Exception("Accepted invalid policy"); }
            catch (TaskApprovalException ex) { Check(ex.ProblemCode == code && (!beforeHost || calls == observer.Calls), code); }
        }
        var original = await reader.ReadAsync(Principal(), default);
        var hostReads = observer.Calls;
        Check(reader.Read(Principal(), "host").Identity == original.Identity && observer.Calls == hostReads,
            "stage policy checks current local identity without calling the runtime host observer");
        var approving = new TaskApproverPolicyReader(provider.GetRequiredService<IServiceScopeFactory>());
        try { approving.Validate("linux:1000"); throw new Exception("Accepted standard approving account"); }
        catch (TaskApprovalException) { Check(true, "approving account must still be a host administrator"); }
        privilege.Value = HostAccountPrivilege.HostRoot;
        approving.Validate("linux:1000");
        Check(true, "canonical approving account is reclassified using current host membership");
        foreach (var changedApprover in new[] { "windows:1000", "linux:other", "display-name" })
        {
            try { approving.Validate(changedApprover); throw new Exception("Accepted retargeted approving identity"); }
            catch (TaskApprovalException) { Check(true, "approving identity cannot change platform, UID or canonical format"); }
        }
        privilege.Value = HostAccountPrivilege.StandardUser;
        Check(original.Identity.HostId == "host" && original.Identity.CanonicalSubject == user.Id.ToString("D")
            && original.Requester.Uid == "1000" && original.ValidUntil == workspace.ControllerLeaseExpiresAt,
            "canonical host identity and controller lease bound policy evidence");
        workspace.ControllerLeaseExpiresAt = clock.Now.AddMinutes(6);
        Check((await reader.ReadAsync(Principal(), default)).Identity.PolicyRevision == original.Identity.PolicyRevision,
            "lease extension does not silently change the semantic policy fingerprint");
        await Reject(Principal("observer"), "task.permission_denied");
        await Reject(Principal("controller", new Claim("installation:install", "docker")), "task.permission_denied");
        await Reject(Principal("controller", new Claim("sub", Guid.NewGuid().ToString("D"))), "task.session_unavailable");
        await Reject(Principal("controller", new Claim("sid", Guid.NewGuid().ToString("D"))), "task.session_unavailable");
        await Reject(Principal("controller", new Claim("exp", "1")), "task.session_unavailable");
        user.SecurityVersion++;
        await Reject(Principal(), "task.session_unavailable"); user.SecurityVersion--;
        session.Status = SessionStatus.Expired;
        await Reject(Principal(), "task.session_unavailable"); session.Status = SessionStatus.Active;
        workspace.ControllerDeviceId = Guid.NewGuid();
        await Reject(Principal(), "task.permission_denied"); workspace.ControllerDeviceId = device.Id;
        workspace.ControllerLeaseExpiresAt = clock.Now;
        await Reject(Principal(), "task.permission_denied"); workspace.ControllerLeaseExpiresAt = clock.Now.AddMinutes(5);
        mode.Enabled = false;
        await Reject(Principal(), "task.permission_denied"); mode.Enabled = true;
        identities.Unavailable = true;
        await Reject(Principal(), "task.identity_unavailable"); identities.Unavailable = false;
        observer.DuringRead = () => privilege.Value = HostAccountPrivilege.HostRoot;
        await Reject(Principal(), "task.policy_changed", false);
        observer.DuringRead = () => session.Status = SessionStatus.Expired;
        await Reject(Principal(), "task.session_unavailable", false);
        session.Status = SessionStatus.Active; observer.DuringRead = null;
        var validity = provider.GetRequiredService<SessionValidityService>();
        var stranger = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", Guid.NewGuid().ToString("D")),
            new Claim("sid", session.Id.ToString("D")) }, "fixture"));
        validity.ExpireSession(stranger);
        Check(session.Status == SessionStatus.Active, "logout cannot expire another user's session");
        validity.ExpireSession(Principal());
        await Reject(Principal(), "task.session_unavailable");
        return count;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1792800000);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Observer : ITaskRuntimeHostObserver
    {
        public int Calls; public Action? DuringRead;
        public Task<RuntimeInstallationSnapshot> ReadAsync(CancellationToken ct)
        { Calls++; DuringRead?.Invoke(); return Task.FromResult(new RuntimeChangeFixture().Installation); }
    }
    private sealed class HostModeFixture : IServerModeResolver
    {
        public bool Enabled = true;
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => Enabled;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
    private sealed class Privilege : IHostAccountPrivilegeService
    {
        public HostAccountPrivilege Value = HostAccountPrivilege.StandardUser;
        public bool IsRoot(ClaimsPrincipal principal) => Value == HostAccountPrivilege.HostRoot;
        public HostAccountPrivilege Classify(ClaimsPrincipal principal) => Value;
        public HostAccountPrivilege Classify(PlatformUserInfo identity) => Value;
    }
    private sealed class Identities : IIdentityProvider
    {
        public bool Unavailable;
        private readonly PlatformUserInfo _identity = new("1000", "alice", HostPlatformKind.Linux, "Alice", null);
        public IdentityLookup Lookup(string identifier) => new(Unavailable ? IdentityLookupStatus.Unavailable : IdentityLookupStatus.Found, _identity);
        public IdentityLookup LookupIdentity(string identity) => Lookup(identity);
        public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity) => new(true);
        public PlatformUserInfo GetUserInfo(string username) => throw new NotSupportedException();
        public CredentialVerifyResult Verify(string username, string password) => throw new NotSupportedException();
    }
}
