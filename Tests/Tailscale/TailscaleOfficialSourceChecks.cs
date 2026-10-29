using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class TailscaleOfficialSourceChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale official source assertion " + count); count++; }
        void Reject(Action action, string code = "component.tailscale_metadata_invalid")
        {
            try { action(); throw new Exception("Invalid source accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == code); }
        }
        const string json = """{"MSIsVersion":"1.104.2","MSIs":{"amd64":"tailscale-setup-1.104.2-amd64.msi","arm64":"tailscale-setup-1.104.2-arm64.msi"},"Version":"1.104.2","Exes":[]}""";
        byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
        var amd = TailscaleOfficialReleases.ParseMetadata(Utf8(json),"win-x64");
        var arm = TailscaleOfficialReleases.ParseMetadata(Utf8(json),"win-arm64");
        Check(amd.Version == "1.104.2" && amd.Format == "msi" && amd.Sha256 is null && amd.DownloadUrl == TailscaleOfficialReleases.Root+amd.FileName);
        Check(arm.FileName == "tailscale-setup-1.104.2-arm64.msi" && arm.Rid == "win-arm64");
        foreach (var rid in new[] { "win-x86","linux-x64","osx-arm64","win-AMD64","" })
            Reject(()=>TailscaleOfficialReleases.ParseMetadata(Utf8(json),rid),ComponentProblemCodes.PlatformUnsupported);
        foreach (var malformed in new[] { "{}","[]","null","{",json.Replace("MSIsVersion","OtherVersion"),
            json.Replace("1.104.2","1.105.2"),json.Replace("1.104.2","01.104.2"),json.Replace("1.104.2","1.104.2-rc1"),
            json.Replace("tailscale-setup-1.104.2-amd64.msi","https://evil.test/installer.msi"),
            json.Replace("tailscale-setup-1.104.2-amd64.msi","../tailscale-setup-1.104.2-amd64.msi"),
            json.Replace("tailscale-setup-1.104.2-amd64.msi","tailscale-setup-1.104.2-arm64.msi"),
            json.Replace("\"MSIsVersion\":","\"MSIsVersion\":\"1.104.2\",\"MSIsVersion\":"),new string(' ',65537) })
            Reject(()=>TailscaleOfficialReleases.ParseMetadata(Utf8(malformed),"win-x64"));
        Reject(()=>TailscaleOfficialReleases.ParseMetadata(Utf8(json),"win-x64","1.102.2"));
        Check(TailscaleOfficialReleases.ParseMetadata(Utf8(json),"win-x64","1.104.2").Version == "1.104.2");
        var digest = new string('A',64);
        foreach (var end in new[] { "","\n","\r\n" }) Check(TailscaleOfficialReleases.ParseChecksum(Utf8(digest+end)) == digest.ToLowerInvariant());
        foreach (var invalid in new[] { "",digest[..63],digest+"  file.msi",digest+"\nother",digest+"\n\n"," "+digest,new string('G',64),new string('A',129) })
            Reject(()=>TailscaleOfficialReleases.ParseChecksum(Utf8(invalid)));
        var calls = new List<(Uri Uri,int Maximum)>();
        var source = new TailscaleOfficialReleases((uri,maximum,ct)=>
        {
            calls.Add((uri,maximum)); ct.ThrowIfCancellationRequested();
            return Task.FromResult(Utf8(uri.AbsolutePath.EndsWith(".sha256") ? digest : json));
        });
        var latest = await source.ResolveAsync("win-x64",null,default);
        Check(latest.Sha256 == digest.ToLowerInvariant() && calls.Count == 2);
        Check(calls[0].Uri.AbsoluteUri == TailscaleOfficialReleases.Root+"?mode=json&os=windows" && calls[0].Maximum == 65536);
        Check(calls[1].Uri.AbsoluteUri == amd.DownloadUrl+".sha256" && calls[1].Maximum == 128);
        calls.Clear();
        await source.ResolveAsync("win-arm64","1.104.2",default);
        Check(calls[0].Uri.Query == "?mode=json&os=windows&v=1.104.2" && calls[1].Uri.AbsoluteUri == arm.DownloadUrl+".sha256");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); calls.Clear();
        try { await source.ResolveAsync("win-x64",null,cancelled.Token); throw new Exception("Cancelled metadata request accepted"); }
        catch (OperationCanceledException) { Check(calls.Count == 0); }
        using var during = new CancellationTokenSource(); var reads = 0;
        var interrupted = new TailscaleOfficialReleases((uri,maximum,ct)=>
        { reads++; during.Cancel(); return Task.FromResult(Utf8(json)); });
        try { await interrupted.ResolveAsync("win-x64",null,during.Token); throw new Exception("Late metadata accepted"); }
        catch (OperationCanceledException) { Check(reads == 1); }
        // File-name guessing and the existing archive installer are not an MSI authorization path.
        Reject(()=>OfficialComponentReleases.Describe(ComponentIds.Tailscale,"win-x64","1.104.2"),ComponentProblemCodes.RequestInvalid);
        Check(ComponentIds.Known(ComponentIds.Tailscale));
        Check(!RuntimeValidation.Https(TailscaleOfficialReleases.Root + "?mode=json&os=windows",out _));
        calls.Clear();
        try
        {
            await source.ResolveAsync("win-x64",null,default,new RuntimeDownloadProxy("file:///proxy",[]));
            throw new Exception("Invalid proxy was bypassed");
        }
        catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.download_proxy_invalid" && calls.Count == 0); }
        Console.WriteLine($"{count} Tailscale official MSI discovery checks passed.");
    }
}
