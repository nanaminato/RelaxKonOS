using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Server.Files;

internal static class WindowsPersonalAuthorizationChecks
{
    // Exercise the policy on Windows Server too; production startup still rejects personal mode there.
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var identities = new PersonalTestIdentities();
        var identity = identities.LookupIdentity(ServerProcessIdentity.CurrentStableIdentity()!).Identity!;
        var users = new InMemoryUserRepository();
        var canonical = new CanonicalUserResolver(identities, users, new InMemoryAliasCredentialRepository(), new AuthSessionStore());
        var owner = canonical.ResolveSystem(identity);
        var mode = new PersonalPolicyMode();
        var helper = new DisabledPrivilegedOperationTransport();
        var privileges = new HostAccountPrivilegeService(helper, users, canonical, mode);
        foreach (var method in new[] { "system", "owner-device-key", "alias" })
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", owner.Id.ToString()), new Claim("amr", method),
                new Claim("jti", Guid.NewGuid().ToString()), new Claim("role", "controller")], "test"));
            var expected = method == "alias" ? HostAccountPrivilege.StandardUser : HostAccountPrivilege.HostAdministrator;
            if (privileges.Classify(principal) != expected) throw new Exception("Personal ownership must grant administrator capability only through a trusted owner identity.");
            var grants = new HostElevationSessionStore(privileges, mode, new HostElevationSessionState());
            if (grants.IsGranted(principal, HostElevationCapability.HostEnvironmentChange, "host/environment/machine") != (method != "alias"))
                throw new Exception("Personal owner operations must authorize without Windows administrator credentials.");
            if (method != "alias")
            {
                var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
                var files = new UserExecutionFileService(new LocalFileService(mode),
                    new UserExecutionContextResolver(users, canonical, mode), new DisabledUserExecutionTransport(), mode, http,
                    new HostFileAuthorizationService(privileges, new FileElevationSessionStore(grants), mode), new PrivilegedFileService(helper));
                if (files.GetInfo(identity.HomeDirectory!) is null)
                    throw new Exception("Personal files must work in-process while the ordinary user Helper backend is disabled.");
            }
        }
        if (privileges.Classify(identity with { Uid = "S-1-5-21-1-2-3-9999" }) != HostAccountPrivilege.StandardUser)
            throw new Exception("Another Windows account cannot inherit personal owner privileges.");
        Console.WriteLine("Personal owner administrator, paired-device authorization, alias isolation and in-process file checks passed.");
    }

    private sealed class PersonalPolicyMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.User;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => new ServerModeResolver(new ConfigurationBuilder().Build(), UserExecutionBackend.Helper).Describe() with { Mode = ServerMode.User };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class PersonalTestIdentities : IIdentityProvider
    {
        // The sandbox execution account need not have a registered Windows profile. Provide an
        // explicit fixture profile; production still requires the native provider's verified profile.
        private readonly PlatformUserInfo _owner = new(ServerProcessIdentity.CurrentStableIdentity()!,
            System.Security.Principal.WindowsIdentity.GetCurrent().Name, HostPlatformKind.Windows,
            Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        public PlatformUserInfo GetUserInfo(string username) => Lookup(username).Identity!;
        public IdentityLookup Lookup(string identifier) => identifier == _owner.Username ? new(IdentityLookupStatus.Found, _owner) : new(IdentityLookupStatus.NotFound);
        public IdentityLookup LookupIdentity(string identity) => identity == _owner.Uid ? new(IdentityLookupStatus.Found, _owner) : new(IdentityLookupStatus.NotFound);
        public CredentialVerifyResult Verify(string username, string password) => CredentialVerifyResult.Failed("No password authentication in this fixture", CredentialError.BadCredentials);
        public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity) => new(true);
    }
}
