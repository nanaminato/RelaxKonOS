using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public static partial class PrivilegedOperationExecutor
{
    // The publisher's own signed apt root. It is a flat repository (no dists/<suite>), so the source
    // line uses the `* *` form the publisher documents; apt then reports its suite/area as `*`.
    private const string SingBoxSourceRoot = "https://deb.sagernet.org/";
    private const string SingBoxSourceKeyUri = "https://deb.sagernet.org/gpg.key";
    private const string SingBoxKeyPath = "/etc/apt/keyrings/relaxkonos-sing-box.gpg";
    private const string SingBoxListPath = "/etc/apt/sources.list.d/relaxkonos-sing-box.list";
    private const string SingBoxKeySha256 = "2AA0A03642D5F72D3FC65919D7C535E9FB9A188565163BA151AD0040A73DED3C";
    /// <summary>Digest of the armored download itself, so a changed key is refused before dearmoring.</summary>
    private const string SingBoxArmoredKeySha256 = "803D5A2F09FE9D360008161AA2684E7F49A211D48A4116D0651B08BDD90BDEA1";

    private static string? SingBoxArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => null
    };

    private static string SingBoxSourceText(string architecture) =>
        $"# RelaxKonOS managed sing-box source\n"
        + $"deb [arch={architecture} signed-by={SingBoxKeyPath}] {SingBoxSourceRoot} * *\n";

    /// <summary>
    /// True only when the host holds exactly the trust this build knows how to verify: the publisher's
    /// binary keyring (pinned digest) and our own source line, byte for byte.
    /// </summary>
    private static bool SingBoxPackagePolicyReady()
    {
        if (SingBoxArchitecture() is not { } architecture) return false;
        RequirePackagePath(SingBoxKeyPath); RequirePackagePath(SingBoxListPath);
        return File.Exists(SingBoxKeyPath) && File.Exists(SingBoxListPath)
            && new FileInfo(SingBoxKeyPath).Length is > 0 and <= 65536
            && new FileInfo(SingBoxListPath).Length is > 0 and <= 4096
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SingBoxKeyPath))) == SingBoxKeySha256
            && File.ReadAllText(SingBoxListPath) == SingBoxSourceText(architecture);
    }

    private static async Task ConfigureSingBoxPackageSourceAsync()
    {
        if (SingBoxArchitecture() is not { } architecture) throw new IOException("Unsupported sing-box package architecture.");
        RequirePackagePath(SingBoxKeyPath); RequirePackagePath(SingBoxListPath);
        // An administrator's existing trust is never overwritten or silently rotated.
        if (File.Exists(SingBoxListPath) && (new FileInfo(SingBoxListPath).Length > 4096
                || File.ReadAllText(SingBoxListPath) != SingBoxSourceText(architecture))
            || File.Exists(SingBoxKeyPath) && (new FileInfo(SingBoxKeyPath).Length > 65536
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SingBoxKeyPath))) != SingBoxKeySha256))
            throw new IOException("Existing sing-box source requires administrator review.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = 131072 };
        var armored = await client.GetByteArrayAsync(SingBoxSourceKeyUri);
        if (Convert.ToHexString(SHA256.HashData(armored)) != SingBoxArmoredKeySha256)
            throw new IOException("Official sing-box signing key mismatch.");
        var keyring = Dearmor(armored);
        if (keyring is null || Convert.ToHexString(SHA256.HashData(keyring)) != SingBoxKeySha256)
            throw new IOException("Official sing-box signing key mismatch.");
        foreach (var (path, content) in new[]
        {
            (SingBoxKeyPath, keyring),
            (SingBoxListPath, Encoding.UTF8.GetBytes(SingBoxSourceText(architecture)))
        })
        {
            RequirePackagePath(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) continue;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                    stream.Write(content);
                    stream.Flush(true);
                }
                File.Move(temporary, path, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    /// <summary>
    /// apt wants a binary keyring. The publisher ships an ASCII-armored key, so the armor is removed
    /// here instead of shelling out to gpg: only the body lines between the BEGIN/END markers are
    /// base64, and the trailing <c>=checksum</c> line is not.
    /// </summary>
    internal static byte[]? Dearmor(byte[] armored)
    {
        if (armored is not { Length: > 0 and <= 131072 }) return null;
        var text = Encoding.ASCII.GetString(armored);
        var body = new StringBuilder();
        var inside = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("-----BEGIN PGP", StringComparison.Ordinal)) { inside = true; continue; }
            if (line.StartsWith("-----END PGP", StringComparison.Ordinal)) break;
            if (!inside || line.Length == 0 || line.StartsWith('=')) continue;
            if (line.StartsWith("Version:", StringComparison.Ordinal) || line.StartsWith("Comment:", StringComparison.Ordinal)) continue;
            body.Append(line);
        }
        if (body.Length == 0) return null;
        try { return Convert.FromBase64String(body.ToString()); }
        catch (FormatException) { return null; }
    }
}
