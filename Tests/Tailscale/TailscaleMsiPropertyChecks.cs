using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class TailscaleMsiPropertyChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("MSI property assertion "+count); count++; }
        var release = new ComponentDownloadRelease("1.104.4","win-x64",TailscaleOfficialReleases.Root+"tailscale-setup-1.104.4-amd64.msi",
            "tailscale-setup-1.104.4-amd64.msi","msi",new string('a',64));
        var valid = new TailscaleMsiProperties.Evidence("Tailscale","Tailscale Inc.","1.104.4",
            "{54939609-8ED7-550F-95F0-CDE77EC4499A}","{C8F0A9CE-39DE-47FE-93B4-F33AC23790B5}",
            "{24386ABE-CC73-4964-9DFD-CC19646DC31B}","x64;1033");
        TailscaleMsiProperties.Validate(valid,release); Check(true);
        foreach (var bad in new[] { valid with { ProductName = "Other" },valid with { Manufacturer = "Other" },
            valid with { Version = "1.104.2" },valid with { Template = "Arm64;1033" },valid with { Template = "Intel;1033" },
            valid with { Template = "x64;1033;extra" },valid with { ProductCode = Guid.Empty.ToString("B") },
            valid with { PackageCode = "invalid" },valid with { UpgradeCode = "" },
            valid with { ProductCode = valid.ProductCode.ToLowerInvariant() },valid with { ProductCode = valid.UpgradeCode },
            valid with { PackageCode = valid.ProductCode },valid with { PackageCode = valid.UpgradeCode } })
        {
            try { TailscaleMsiProperties.Validate(bad,release); throw new Exception("Bad MSI identity accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_msi_properties_invalid"); }
        }
        var arm = release with { Rid = "win-arm64",FileName = "tailscale-setup-1.104.4-arm64.msi",DownloadUrl = TailscaleOfficialReleases.Root+"tailscale-setup-1.104.4-arm64.msi" };
        TailscaleMsiProperties.Validate(valid with { Template = "Arm64;1033" },arm); Check(true);
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(Path.GetTempPath(),"tailscale-msi-properties-"+Guid.NewGuid().ToString("N")+".msi");
            try
            {
                File.WriteAllText(path,"Not an MSI database");
                try { TailscaleMsiProperties.Read(path); throw new Exception("Invalid database accepted"); }
                catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_msi_properties_invalid"); }
                File.Move(path,path+".released"); File.Move(path+".released",path); Check(true);
            }
            finally { File.Delete(path); }
        }
        Console.WriteLine($"Tailscale MSI property checks passed: {count}");
    }
}
