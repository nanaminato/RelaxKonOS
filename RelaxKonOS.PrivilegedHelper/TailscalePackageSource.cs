using System.Security.Cryptography;
using RelaxKonOS.Protocol.ManagedRuntimes;

using RelaxKonOS.PrivilegedHelper;

public static partial class PrivilegedOperationExecutor
{
    private const string TailscaleKeyPath = "/usr/share/keyrings/tailscale-archive-keyring.gpg";
    private const string TailscaleListPath = "/etc/apt/sources.list.d/tailscale.list";
    private const string TailscaleKeySha256 = "3E03DACF222698C60B8E2F990B809CA1B3E104DE127767864284E6C228F1FB39";

    private static (string Distribution, string Suite) TailscalePackagePlatform()
    {
        var values = File.ReadAllLines("/etc/os-release").Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1].Trim('"'));
        return (values.GetValueOrDefault("ID"), values.GetValueOrDefault("VERSION_ID")) switch
        {
            ("ubuntu", "22.04") => ("ubuntu", "jammy"),
            ("ubuntu", "24.04") => ("ubuntu", "noble"),
            ("ubuntu", "26.04") => ("ubuntu", "resolute"),
            ("debian", "12") => ("debian", "bookworm"),
            ("debian", "13") => ("debian", "trixie"),
            _ => throw new IOException("Unsupported official Tailscale package distribution.")
        };
    }
    private static string TailscaleSourceText(string distribution, string suite) =>
        $"# Tailscale packages for {distribution} {suite}\ndeb [signed-by={TailscaleKeyPath}] https://pkgs.tailscale.com/stable/{distribution} {suite} main\n";

    private static bool TailscalePackagePolicyReady()
    {
        var (distribution, suite) = TailscalePackagePlatform();
        RequirePackagePath(TailscaleKeyPath); RequirePackagePath(TailscaleListPath);
        foreach (var path in new[] { TailscaleKeyPath, TailscaleListPath })
            TailscaleControlProtection.Require(File.Exists(path) ? path : Path.GetDirectoryName(path)!);
        return File.Exists(TailscaleKeyPath) && File.Exists(TailscaleListPath)
            && new FileInfo(TailscaleKeyPath).Length is > 0 and <= 65536
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(TailscaleKeyPath))) == TailscaleKeySha256
            && File.ReadAllText(TailscaleListPath) == TailscaleSourceText(distribution, suite);
    }
    private static bool TailscalePackageCandidateAllowed(ComponentPackageTarget target)
    {
        var (distribution, suite) = TailscalePackagePlatform();
        var root = "https://pkgs.tailscale.com/stable/" + distribution;
        return target.PackageName == "tailscale" && target.BaseUri.TrimEnd('/') == root && target.Suite == suite
            && target.Area == "main" && Uri.TryCreate(target.PoolUri, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && target.PoolUri.StartsWith(root + "/pool/", StringComparison.Ordinal);
    }

    private static async Task ConfigureTailscalePackageSourceAsync()
    {
        var (distribution, suite) = TailscalePackagePlatform();
        RequirePackagePath(TailscaleKeyPath); RequirePackagePath(TailscaleListPath);
        foreach (var path in new[] { TailscaleKeyPath, TailscaleListPath })
            TailscaleControlProtection.Require(File.Exists(path) ? path : Path.GetDirectoryName(path)!);
        var source = TailscaleSourceText(distribution, suite);
        // Existing administrator trust is never overwritten or silently rotated.
        if (File.Exists(TailscaleListPath) && File.ReadAllText(TailscaleListPath) != source
            || File.Exists(TailscaleKeyPath) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(TailscaleKeyPath))) != TailscaleKeySha256)
            throw new IOException("Existing Tailscale source requires administrator review.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = 65536 };
        var bytes = await client.GetByteArrayAsync($"https://pkgs.tailscale.com/stable/{distribution}/{suite}.noarmor.gpg");
        if (Convert.ToHexString(SHA256.HashData(bytes)) != TailscaleKeySha256)
            throw new IOException("Official Tailscale signing key mismatch.");
        foreach (var (path, content) in new[] { (TailscaleKeyPath, bytes), (TailscaleListPath, System.Text.Encoding.UTF8.GetBytes(source)) })
        {
            RequirePackagePath(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) continue;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead); stream.Write(content); stream.Flush(true); }
                File.Move(temporary, path, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
