using System.Security.Claims;
using System.Security.Principal;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

public static class HostOperationAuthorizationChecks
{
    public static void Run()
    {
        var privileges = new TestHostAccountPrivilegeService();
        var store = new HostElevationSessionStore(privileges, new UploadSessionChecks.SystemMode());
        var system = Principal("system", "system-token");
        var alias = Principal("alias", "alias-token");
        var capability = HostElevationCapability.HostEnvironmentChange;
        const string machine = "host/environment/machine";
        Assert(!store.IsGranted(system, capability, machine), "Standard accounts require authorization");
        foreach (var level in new[] { HostAccountPrivilege.HostAdministrator, HostAccountPrivilege.HostRoot })
        {
            privileges.Level = level;
            Assert(store.IsGranted(system, capability, machine), "System-authenticated administrators skip repeat passwords");
            Assert(!store.IsGranted(alias, capability, machine), "Alias authentication cannot inherit host privilege");
            Assert(!store.IsGranted(system, HostElevationCapability.FileRead, Path.GetFullPath("protected")),
                "File capabilities retain independent authorization sources and root policies");
            Assert(!store.IsGranted(system, (HostElevationCapability)int.MaxValue, machine), "Unknown capabilities fail closed");
        }
        privileges.Level = HostAccountPrivilege.StandardUser;
        Assert(!store.IsGranted(system, capability, machine), "Revoked administrator status leaves no cached auto grant");
        privileges.Unavailable = true;
        try { store.IsGranted(system, capability, machine); throw new Exception("Unavailable host policy allowed access"); }
        catch (InvalidOperationException) { }
        privileges.Unavailable = false;
        store.Grant(alias, capability, machine, false, "linux-pam-administrator");
        Assert(store.IsGranted(alias, capability, machine), "Explicit administrator credentials authorize an alias session");
        Assert(!store.IsGranted(alias, HostElevationCapability.HostEnvironmentRead, machine), "Manual grants are capability scoped");
        Assert(!store.IsGranted(alias, capability, "host/environment/user/1000"), "Manual grants are target scoped");
        Assert(!store.IsGranted(Principal("alias", "refreshed-token"), capability, machine), "Grants are token scoped");
        store.Revoke(alias);
        Assert(!store.IsGranted(alias, capability, machine), "Revocation removes manual grants");
        privileges.Level = HostAccountPrivilege.HostAdministrator;
        var userMode = new HostElevationSessionStore(privileges, new TestUserModeResolver());
        try { userMode.Grant(system, capability, machine, false, "test"); throw new Exception("User Mode accepted a host grant"); }
        catch (InvalidOperationException) { }
        Assert(!userMode.IsGranted(system, capability, machine), "User Mode cannot use automatic or manual host grants");
        if (OperatingSystem.IsWindows()) VerifyWindowsMembership();
        Console.WriteLine("Host operation authorization checks passed.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyWindowsMembership()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var type = typeof(RelaxKonOS.Server.Identity.WindowsLogonProvider).Assembly
            .GetType("RelaxKonOS.Server.Identity.WindowsAdministratorMembership")!;
        var actual = (bool)type.GetMethod("IsAccountAdministrator")!.Invoke(null, [identity.User!.Value])!;
        var expected = (bool)type.GetMethod("IsAdministratorOrCanElevate")!.Invoke(null, [identity])!;
        Assert(actual == expected, "Canonical Windows SID membership agrees with the current UAC token");
    }

    private static ClaimsPrincipal Principal(string method, string token) => new(new ClaimsIdentity([
        new Claim("sub", "test-user"), new Claim("jti", token), new Claim("amr", method)], "test"));
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
