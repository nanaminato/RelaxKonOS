using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class TailscaleSignatureChecks
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale signature assertion " + checks); checks++; }
        const string subject = "CN=Tailscale Inc., O=Tailscale Inc., L=Toronto, S=Ontario, C=CA, SERIALNUMBER=1131559-5, OID.2.5.4.15=Private Organization, OID.1.3.6.1.4.1.311.60.2.1.3=CA";
        bool Matches(string name) => TailscaleMsiSignature.PublisherMatches(new X500DistinguishedName(name).RawData);
        Check(Matches(subject));
        Check(Matches(string.Join(", ",subject.Split(", ").Reverse())));
        foreach (var wrong in new[] { subject.Replace("1131559-5","1131559-6"),subject.Replace("Toronto","Other"),
            subject.Replace("Ontario","Other"),subject.Replace("Private Organization","Other"),subject.Replace("C=CA","C=US"),
            subject.Replace("CN=Tailscale Inc.","CN=Other"),subject.Replace("O=Tailscale Inc.","O=Other"),
            subject.Replace("OID.1.3.6.1.4.1.311.60.2.1.3=CA","OID.1.3.6.1.4.1.311.60.2.1.3=US"),
            "CN=Tailscale Inc., O=Tailscale Inc.",subject+", CN=Tailscale Inc.",subject+", OU=Extra" }) Check(!Matches(wrong));
        Check(!TailscaleMsiSignature.PublisherMatches(new byte[] { 1,2,3 }));
        Check(!TailscaleMsiSignature.PublisherMatches(new X500DistinguishedName(subject).RawData.Concat(new byte[] { 0 }).ToArray()));
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(Path.GetTempPath(),"tailscale-signature-"+Guid.NewGuid().ToString("N")+".msi");
            byte[] bytes = [1,2,3,4];
            var release = new ComponentDownloadRelease("1.104.4","win-x64",TailscaleOfficialReleases.Root+"tailscale-setup-1.104.4-amd64.msi",
                "tailscale-setup-1.104.4-amd64.msi","msi",Convert.ToHexStringLower(SHA256.HashData(bytes)));
            try
            {
                await File.WriteAllBytesAsync(path,bytes);
                try { await TailscaleMsiSignature.InspectAsync(path,release,default); throw new Exception("Unsigned MSI accepted"); }
                catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_signature_invalid"); }
                try { await TailscaleMsiSignature.InspectAsync(path,release with { Sha256 = new string('0',64) },default); throw new Exception("Wrong MSI hash accepted"); }
                catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_signature_invalid"); }
            }
            finally { File.Delete(path); }
        }
        Console.WriteLine($"Tailscale MSI signature checks passed: {checks}");
    }
}
