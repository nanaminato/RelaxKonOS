using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Identity;

internal static class WindowsOwnerBootstrapChecks
{
    public static void Run()
    {
        const string ownerSid = "S-1-5-21-123-456-789-1001";
        const string anotherSid = "S-1-5-21-123-456-789-1002";
        foreach (var serviceSid in new[] { "S-1-5-18", "S-1-5-19", "S-1-5-20" })
        {
            if (!WindowsOwnerBootstrapPolicy.AllowsAccount(ownerSid, serviceSid, ServerMode.System))
                throw new Exception("A verified administrator must be able to pair under a built-in Windows service identity.");
            if (WindowsOwnerBootstrapPolicy.AllowsAccount(ownerSid, serviceSid, ServerMode.User) ||
                WindowsOwnerBootstrapPolicy.AllowsAccount(serviceSid, serviceSid, ServerMode.System))
                throw new Exception("Service-mode enrollment must not allow User Mode or service-account device ownership.");
        }
        if (!WindowsOwnerBootstrapPolicy.AllowsAccount(ownerSid, ownerSid, ServerMode.System) ||
            WindowsOwnerBootstrapPolicy.AllowsAccount(anotherSid, ownerSid, ServerMode.System) ||
            WindowsOwnerBootstrapPolicy.AllowsAccount(ownerSid, null, ServerMode.System))
            throw new Exception("Desktop enrollment must remain bound to its own Windows account and known process identity.");
        Console.WriteLine("Windows service owner-device account scope checks passed.");
    }
}
