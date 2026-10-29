using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class TailscalePinChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale pin assertion "+count); count++; }
        void Reject(Action action,string? code = null)
        {
            try { action(); throw new Exception("Conflicting or corrupt pin accepted"); }
            catch (RuntimePolicyException ex) { Check(code is null || ex.ProblemCode == code); }
        }
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(temporary,"tailscale-pins-"+Guid.NewGuid().ToString("N")));
        var release = new ComponentDownloadRelease("1.104.4","win-x64",TailscaleOfficialReleases.Root+"tailscale-setup-1.104.4-amd64.msi",
            "tailscale-setup-1.104.4-amd64.msi","msi",new string('a',64));
        var properties = new TailscaleMsiProperties.Evidence("Tailscale","Tailscale Inc.",release.Version,
            "{54939609-8ED7-550F-95F0-CDE77EC4499A}","{C8F0A9CE-39DE-47FE-93B4-F33AC23790B5}",
            "{24386ABE-CC73-4964-9DFD-CC19646DC31B}","x64;1033");
        var evidence = new TailscaleMsiSignature.Evidence(release.Sha256!,"Tailscale Inc. / CA / 1131559-5",new string('B',64),properties);
        try
        {
            Directory.CreateDirectory(root); var store = new TailscaleMsiPins(root,_ => { });
            store.Check(release); Check(!Directory.Exists(Path.Combine(root,"pins")));
            Reject(() => store.Remember(release,evidence with { Publisher = "Other" }));
            Check(!Directory.Exists(Path.Combine(root,"pins")));
            store.Remember(release,evidence);
            var path = Path.Combine(root,"pins","win-x64-1.104.4.json"); var original = File.ReadAllText(path);
            Check(JsonSerializer.Deserialize<TailscaleMsiPins.Pin>(original)?.Evidence == evidence);
            new TailscaleMsiPins(root,_ => { }).Check(release);
            store.Remember(release,evidence); Check(File.ReadAllText(path) == original);
            Reject(() => store.Check(release with { Sha256 = new string('c',64) }),"component.tailscale_artifact_conflict");
            Reject(() => store.Remember(release,evidence with { CertificateSha256 = new string('C',64) }),"component.tailscale_artifact_conflict");
            Check(File.ReadAllText(path) == original);
            foreach (var malformed in new[] { "", "{}", "null", "{", new string(' ',16385),
                original[..^1]+",\"Unknown\":true}",original.Replace("\"Release\":","\"Release\":null,\"Release\":"),
                original.Replace("\"CertificateSha256\":","\"OtherCertificate\":"),
                original.Replace("1131559-5","1131559-6") })
            {
                File.WriteAllText(path,malformed); Reject(() => store.Check(release)); Check(File.ReadAllText(path) == malformed);
            }
            File.WriteAllText(path,original);
            var copied = false; var id = Guid.NewGuid();
            var stage = new TailscaleMsiStaging(root,p => Directory.CreateDirectory(p),_ => { },
                (_,_,_,_) => Task.FromResult(release with { Sha256 = new string('c',64) }),
                (_,_,_,_) => { copied = true; return Task.FromResult(0L); },(_,_,_) => Task.FromResult(evidence));
            try { using var lease = await stage.PrepareAsync(id,release.Rid,release.Version,null,default); throw new Exception("Changed release staged"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_artifact_conflict" && !copied); }
            Check(!Directory.Exists(Path.Combine(root,id.ToString("N"))) && File.ReadAllText(path) == original);
        }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(temporary) || !Path.GetFileName(root).StartsWith("tailscale-pins-",StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected pin test cleanup target");
            if (Directory.Exists(root)) Directory.Delete(root,true);
        }
        Console.WriteLine($"Tailscale immutable artifact pin checks passed: {count}");
    }
}
