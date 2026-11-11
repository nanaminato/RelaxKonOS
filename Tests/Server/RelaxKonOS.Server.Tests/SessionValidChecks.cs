using System.Security.Claims;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;

internal static class SessionValidChecks
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var settings = new ServerRuntimeSettingsStore(Path.Combine(root, "runtime.json"));
        using var state = new HostElevationSessionState(settings);
        var privilege = new TestHostAccountPrivilegeService();
        var store = new HostElevationSessionStore(privilege, new UploadSessionChecks.SystemMode(), state);
        var principal = Principal();
        const HostElevationCapability original = HostElevationCapability.ProcessManage;
        const HostElevationCapability other = HostElevationCapability.CertificateCreateSelfSigned;
        const string target = "certificates/self-signed";
        void Verify(bool passed, string message)
        { if (!passed) throw new Exception(message); Console.WriteLine("PASS sessionValid: " + message); }
        void Configure(bool enabled)
        {
            var current = settings.Current;
            settings.Save(new(current.InstanceId, "sessionValid", JsonSerializer.SerializeToElement(enabled), current.Revision), Guid.NewGuid());
        }
        Verify(settings.Current.Settings.SessionValid, "enabled by default");
        Verify(!store.IsGranted(principal, other, target), "no grant before administrator verification");
        store.Grant(principal, original, "host/processes", false, "linux-pam-administrator");
        Verify(store.IsGranted(principal, other, target), "verified token authorizes another capability and resource");
        Verify(store.IsGranted(principal, HostElevationCapability.FileWrite, Path.GetFullPath("protected")),
            "verified token also authorizes the file grant layer");
        Verify(!store.IsGranted(Principal(token: "refreshed-token"), other, target), "new and refreshed tokens do not inherit verification");
        Verify(!store.IsGranted(Principal(subject: Guid.NewGuid().ToString()), other, target), "subject cannot borrow another token grant");
        Verify(!store.IsGranted(Principal(session: Guid.NewGuid().ToString()), other, target), "grant is bound to the authenticated session");
        Verify(!store.IsGranted(Principal(version: "2"), other, target), "security version changes cannot reuse verification");
        Verify(!store.IsGranted(Principal(expiry: DateTimeOffset.UtcNow.AddSeconds(-1)), other, target), "expired tokens cannot reuse verification");
        Verify(!store.IsGranted(Principal(includeExpiry: false), other, target), "missing token expiry cannot obtain session authority");
        Verify(!store.IsGranted(principal, (HostElevationCapability)int.MaxValue, target), "unknown capabilities remain denied");
        Configure(false);
        Verify(!store.IsGranted(principal, other, target), "disabling takes effect immediately");
        Verify(store.IsGranted(principal, original, "host/processes"), "disabled mode retains the original short scoped grant");
        store.Grant(principal, original, "host/processes", false, "linux-pam-administrator");
        Verify(!store.IsGranted(principal, other, target), "verification while disabled stays capability scoped");
        Configure(true);
        Verify(!store.IsGranted(principal, other, target), "reenabling does not resurrect remembered authority");
        store.Grant(principal, original, "host/processes", false, "linux-pam-administrator");
        Verify(store.IsGranted(principal, other, target), "fresh verification works after reenabling");
        using (var restarted = new HostElevationSessionState(settings))
            Verify(!new HostElevationSessionStore(privilege, new UploadSessionChecks.SystemMode(), restarted).IsGranted(principal, other, target),
                "server restart does not retain verified tokens");
        store.Revoke(principal);
        Verify(!store.IsGranted(principal, other, target) && !store.IsGranted(principal, original, "host/processes"),
            "logout revocation clears broad and scoped grants");
        store.Grant(principal, original, "host/processes", false, "test");
        Verify(!store.IsGranted(principal, other, target), "unrecognized authentication cannot promote a grant");
        Configure(false);
        Verify(!new ServerRuntimeSettingsStore(Path.Combine(root, "runtime.json")).Current.Settings.SessionValid, "switch survives restart");
        var currentSettings = settings.Current;
        try
        {
            settings.Save(new(currentSettings.InstanceId, "sessionValid", JsonSerializer.SerializeToElement(1), currentSettings.Revision), Guid.NewGuid());
            throw new Exception("numeric sessionValid accepted");
        }
        catch (ArgumentException) { Verify(true, "setting requires a boolean"); }
        var users = new RelaxKonOS.Server.Storage.InMemoryUserRepository();
        var sessions = new RelaxKonOS.Server.Storage.InMemorySessionRepository();
        var user = users.Add(new() { Id = Guid.Parse(Subject), SecurityVersion = 1 });
        var login = sessions.Add(new() { Id = Guid.Parse(Session), UserId = user.Id,
            AuthenticationMethod = "alias", Status = RelaxKonOS.Protocol.Workspace.SessionStatus.Active });
        var services = new ServiceCollection();
        services.AddSingleton<RelaxKonOS.Server.Storage.IUserRepository>(users);
        services.AddSingleton<RelaxKonOS.Server.Storage.ISessionRepository>(sessions);
        using var provider = services.BuildServiceProvider();
        var validity = new RelaxKonOS.Server.Identity.SessionValidityService(provider.GetRequiredService<IServiceScopeFactory>());
        // Real access tokens also carry their original authentication time.
        ((ClaimsIdentity)principal.Identity!).AddClaim(new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));
        Configure(true);
        using var checkedState = new HostElevationSessionState(settings, validity);
        var checkedStore = new HostElevationSessionStore(privilege, new UploadSessionChecks.SystemMode(), checkedState);
        checkedStore.Grant(principal, original, "host/processes", false, "linux-pam-administrator");
        Verify(checkedStore.IsGranted(principal, other, target), "persisted live session can reuse verification");
        login.Status = RelaxKonOS.Protocol.Workspace.SessionStatus.Expired;
        sessions.Update(login);
        Verify(!checkedStore.IsGranted(principal, other, target) && !checkedStore.IsGranted(principal, original, "host/processes"),
            "persisted session revocation blocks remembered and scoped authorization");
        login.Status = RelaxKonOS.Protocol.Workspace.SessionStatus.Active;
        sessions.Update(login);
        Verify(!checkedStore.IsGranted(principal, other, target), "reactivating a session does not resurrect rejected token memory");
        checkedStore.Grant(principal, original, "host/processes", false, "linux-pam-administrator");
        user.SecurityVersion++;
        users.Update(user);
        Verify(!checkedStore.IsGranted(principal, other, target), "security-version revocation is rechecked against current user state");
    }
    private static readonly string Subject = Guid.NewGuid().ToString();
    private static readonly string Session = Guid.NewGuid().ToString();
    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
    private static ClaimsPrincipal Principal(string token = "verified-token", string? subject = null, string? session = null,
        string version = "1", DateTimeOffset? expiry = null, bool includeExpiry = true)
    {
        var claims = new List<Claim> { new("sub", subject ?? Subject), new("jti", token), new("sid", session ?? Session),
            new("security_version", version), new("amr", "alias") };
        if (includeExpiry) claims.Add(new("exp", (expiry ?? Expiry).ToUnixTimeSeconds().ToString()));
        return new(new ClaimsIdentity(claims, "test"));
    }
}
