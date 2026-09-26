using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

public static class HostFileRoutingChecks
{
    public static void Run()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"));
        var grants = new UploadSessionChecks.RecordingElevationStore();
        var privileges = new MutablePrivileges();
        var routing = new HostFileAuthorizationService(privileges, grants, new UploadSessionChecks.SystemMode());
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "host-file-routing-test"));

        Assert(routing.Authorize(principal, FileElevationCapability.Read, path) is null,
            "Standard accounts without an exact grant do not reach the Helper");
        grants.Granted = true;
        Assert(routing.Authorize(principal, FileElevationCapability.Read, path) == PrivilegedFileAuthorizationSource.ManualGrant,
            "A standard account uses the manual grant source");
        privileges.Level = HostAccountPrivilege.HostAdministrator;
        Assert(routing.Authorize(principal, FileElevationCapability.Read, path) == PrivilegedFileAuthorizationSource.HostAdministrator,
            "A current administrator uses its independent policy even when a manual grant exists");
        privileges.Level = HostAccountPrivilege.HostRoot;
        Assert(routing.Authorize(principal, FileElevationCapability.Read, path) == PrivilegedFileAuthorizationSource.HostRoot,
            "A root session uses only the root policy source");
        privileges.Revoked = true;
        AssertThrows<UnauthorizedAccessException>(() => routing.Authorize(principal, FileElevationCapability.Read, path),
            "Identity drift fails closed before a cached grant is consulted");
        privileges.Revoked = false;
        Assert(new HostFileAuthorizationService(privileges, grants, new TestUserModeResolver())
            .Authorize(principal, FileElevationCapability.Read, path) is null,
            "User Mode cannot reach the privileged file router");
        Console.WriteLine("Host file authorization source checks passed.");
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class MutablePrivileges : IHostAccountPrivilegeService
    {
        public HostAccountPrivilege Level { get; set; }
        public bool Revoked { get; set; }
        public bool IsRoot(ClaimsPrincipal principal) => Level == HostAccountPrivilege.HostRoot && !Revoked;
        public HostAccountPrivilege Classify(PlatformUserInfo identity) => Level;
        public HostAccountPrivilege Classify(ClaimsPrincipal principal)
            => Revoked ? throw new UnauthorizedAccessException("Canonical identity changed") : Level;
    }
}
