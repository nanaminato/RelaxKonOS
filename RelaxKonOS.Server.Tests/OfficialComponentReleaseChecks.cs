using RelaxKonOS.ManagedRuntimes.ComponentPackages;

internal static class OfficialComponentReleaseChecks
{
    internal static async Task RunAsync(bool live)
    {
        var nginx = OfficialComponentReleases.ParseNginx("""
            <a href="/download/nginx-1.28.3.zip">old</a><a href="/download/nginx-1.30.6.zip">stable</a>
            <a href="/download/nginx-1.30.6.zip">duplicate</a><a href="/download/nginx-1.31.8.zip.asc">signature</a>
            """, "win-x64");
        Check(nginx.Select(release => release.Version).SequenceEqual(["1.30.6", "1.28.3"]), "nginx exposes multiple versions in numeric order without duplicates or signatures");
        var json = """
            [
            {"tag_name":"v0.71.0","draft":false,"prerelease":false,"assets":[
              {"name":"frp_0.71.0_windows_amd64.zip","browser_download_url":"https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_amd64.zip","digest":null},
              {"name":"frp_0.71.0_windows_arm64.zip","browser_download_url":"https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_arm64.zip"}]},
            {"tag_name":"nightly","draft":false,"prerelease":true,"assets":[]}
            ]
            """;
        var frp = OfficialComponentReleases.ParseGitHub("frpc", "win-x64", json);
        Check(frp is [{ Version: "v0.71.0", Sha256: null, Rid: "win-x64" }], "GitHub releases select the host asset and do not require upstream digest metadata");
        var arm = OfficialComponentReleases.ParseGitHub("frpc", "win-arm64", json);
        Check(arm is [{ Rid: "win-arm64", FileName: "frp_0.71.0_windows_arm64.zip" }],
            "local discovery uses the requested server ARM64 target instead of the desktop architecture");
        Check(OfficialComponentReleases.ParseGitHub("frpc", "linux-arm64", json).Length == 0,
            "discovery never substitutes a Windows asset for a Linux server");
        Check(OfficialComponentReleases.Describe("frpc", "win-arm64", "v0.70.0") == OfficialComponentReleases.Describe("frps", "win-arm64", "v0.70.0"), "frpc and frps share the selected version's bundle");
        Check(OfficialComponentReleases.Describe("mihomo", "win-x64", "v1.19.30").FileName == "mihomo-windows-amd64-v1.19.30.zip", "Mihomo resolves the chosen version instead of a build-time constant");
        var rejected = false;
        try { OfficialComponentReleases.Describe("frpc", "win-x64", "../../other"); }
        catch (RelaxKonOS.ManagedRuntimes.RuntimePolicyException) { rejected = true; }
        Check(rejected, "selected versions cannot escape the managed runtime directory");
        using var input = new MemoryStream([1, 2, 3]);
        using var output = new MemoryStream();
        await OfficialComponentReleases.CopyAsync(input, output, null, default);
        Check(output.ToArray().SequenceEqual(new byte[] { 1, 2, 3 }), "user-provided archives do not require publisher catalog registration");
        input.Position = 0; output.SetLength(0); rejected = false;
        try { await OfficialComponentReleases.CopyAsync(input, output, new string('0', 64), default); }
        catch (IOException) { rejected = true; }
        Check(rejected, "an available upstream digest still detects incomplete or changed bytes automatically");
        if (live)
        {
            foreach (var id in new[] { "nginx", "mihomo", "frpc", "frps" })
            {
                var releases = await new OfficialComponentReleases().ListAsync(id, "win-x64", true, default);
                Check(releases.Length > 1, id + " official upstream returned " + releases.Length + " selectable Windows versions");
                Console.WriteLine(id + ": " + string.Join(", ", releases.Take(5).Select(release => release.Version)));
            }
        }
    }
    private static void Check(bool success, string message)
    { if (!success) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
