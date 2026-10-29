using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class TailscaleArtifactChecks
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale artifact assertion " + checks); checks++; }
        byte[] bytes = [1,2,3,4];
        var release = new ComponentDownloadRelease("1.104.4","win-x64",TailscaleOfficialReleases.Root+"tailscale-setup-1.104.4-amd64.msi",
            "tailscale-setup-1.104.4-amd64.msi","msi",Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var calls = 0;
        var transfer = new TailscaleMsiArtifactTransfer(async (_,output,_,ct) => { calls++; await output.WriteAsync(bytes,ct); });
        using var output = new MemoryStream();
        Check(await transfer.CopyAsync(release,output,null,default) == bytes.Length && output.Position == 0 && output.ToArray().SequenceEqual(bytes));
        Check(calls == 1);
        foreach (var bad in new[] {
            release with { DownloadUrl = "https://evil.test/installer.msi" }, release with { Rid = "win-arm64" },
            release with { DownloadUrl = release.DownloadUrl+"?token=secret" }, release with { DownloadUrl = release.DownloadUrl+"#x" },
            release with { DownloadUrl = release.DownloadUrl.Replace("https:","http:") },
            release with { FileName = "../installer.msi" }, release with { Version = "1.105.4" },
            release with { Version = "1.104.4-rc1" }, release with { Format = "zip" },
            release with { Sha256 = null }, release with { Sha256 = new string('G',64) } })
        {
            using var rejected = new MemoryStream();
            try { await transfer.CopyAsync(bad,rejected,null,default); throw new Exception("Invalid artifact accepted"); }
            catch (RuntimePolicyException) { Check(rejected.Length == 0 && calls == 1); }
        }
        try { await transfer.CopyAsync(release,output,null,default); throw new Exception("Occupied staging accepted"); }
        catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_staging_invalid" && output.Length == 4 && calls == 1); }
        using var mismatch = new MemoryStream();
        try { await transfer.CopyAsync(release with { Sha256 = new string('0',64) },mismatch,null,default); throw new Exception("Wrong digest accepted"); }
        catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_artifact_invalid" && mismatch.Length == 0 && mismatch.Position == 0); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var previous = calls;
        using var before = new MemoryStream();
        try { await transfer.CopyAsync(release,before,null,cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(calls == previous && before.Length == 0); }
        using var during = new CancellationTokenSource();
        using var partial = new MemoryStream();
        var interrupted = new TailscaleMsiArtifactTransfer(async (_,target,_,ct) => { await target.WriteAsync(bytes,ct); during.Cancel(); });
        try { await interrupted.CopyAsync(release,partial,null,during.Token); throw new Exception("Late cancellation accepted"); }
        catch (OperationCanceledException) { Check(partial.Length == 0 && partial.Position == 0); }
        using var broken = new MemoryStream();
        var failing = new TailscaleMsiArtifactTransfer(async (_,target,_,ct) => { await target.WriteAsync(bytes,ct); throw new IOException("fixture"); });
        try { await failing.CopyAsync(release,broken,null,default); throw new Exception("Partial transfer accepted"); }
        catch (IOException) { Check(broken.Length == 0 && broken.Position == 0); }
        using var empty = new MemoryStream();
        var noBody = new TailscaleMsiArtifactTransfer((_,_,_,_) => Task.CompletedTask);
        try { await noBody.CopyAsync(release,empty,null,default); throw new Exception("Empty artifact accepted"); }
        catch (RuntimePolicyException) { Check(empty.Length == 0); }
        Console.WriteLine($"Tailscale MSI artifact checks passed: {checks}");
    }
}
