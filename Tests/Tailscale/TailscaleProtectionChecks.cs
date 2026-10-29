using System.Security.AccessControl;
using RelaxKonOS.PrivilegedHelper;

internal static class TailscaleProtectionChecks
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var count = 0;
        void Accept(string sddl,bool leaf) { TailscaleControlProtection.RequireWindowsDescriptor(new RawSecurityDescriptor(sddl),leaf); count++; }
        void Reject(string sddl,bool leaf)
        {
            try { TailscaleControlProtection.RequireWindowsDescriptor(new RawSecurityDescriptor(sddl),leaf); throw new Exception("Unsafe Tailscale ACL accepted: "+sddl+" leaf="+leaf); }
            catch (UnauthorizedAccessException) { count++; }
        }
        Accept("O:BAG:BAD:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;WD)",true);
        Accept("O:BAG:BAD:",true); // Empty DACL denies access; it is not a NULL DACL.
        Reject("O:BAG:BA",true);
        Reject("O:BAG:BAD:NO_ACCESS_CONTROL",true);
        Reject("O:WDG:BAD:(A;;FA;;;BA)",true);
        foreach (var mask in new[] { "SD","0x40","WD","WO","GW","GA" })
        {
            Reject("O:BAG:BAD:(A;;"+mask+";;;WD)",false);
            Reject("O:BAG:BAD:(A;;"+mask+";;;WD)",true);
        }
        Accept("O:BAG:BAD:(A;OICIIO;GA;;;WD)(A;;FA;;;BA)",true);
        Accept("O:BAG:BAD:(A;;0x4;;;AU)(A;;FA;;;BA)",false); // Creating siblings does not replace an existing protected child.
        Reject("O:BAG:BAD:(A;;0x4;;;AU)(A;;FA;;;BA)",true);
        Accept("O:BAG:BAD:(D;;GA;;;WD)(A;;FA;;;BA)",true);
        Console.WriteLine($"Tailscale Windows protection checks passed: {count}");
    }
}
