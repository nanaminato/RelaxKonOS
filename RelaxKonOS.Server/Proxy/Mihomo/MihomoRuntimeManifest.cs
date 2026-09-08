using System.Runtime.InteropServices;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Source-controlled Mihomo trust manifest. HTTP callers may choose only Version.</summary>
public sealed class MihomoRuntimeManifest
{
    public const long MaximumArchiveBytes = 128L * 1024 * 1024;
    public const string SupportedVersion = "v1.19.30";

    public IReadOnlyList<MihomoRuntimeRelease> Releases { get; init; } =
    [
        new(SupportedVersion, "win-x64", "mihomo-windows-amd64-v1.19.30.zip", "zip", "22c09fd67673895ef7cd6b1820563918275c3d316f2462b306208675118db3c0"),
        new(SupportedVersion, "win-arm64", "mihomo-windows-arm64-v1.19.30.zip", "zip", "b37c4b0259e85b020edc4215aa4c86052e21071cf520d4800364b21b4e2fc162"),
        new(SupportedVersion, "linux-x64", "mihomo-linux-amd64-v1.19.30.gz", "gz", "cf06ce2c7d1421bdbda14ee4a5b6046672dc35ebf8eecd8e77504ec3c0ed9a84"),
        new(SupportedVersion, "linux-arm64", "mihomo-linux-arm64-v1.19.30.gz", "gz", "58896873736d28628f66de3677c8654fa0f180662523148e136cff4f6e890069"),
    ];

    public MihomoRuntimeRelease? Find(string? version) => Releases.SingleOrDefault(release =>
        release.Version == (string.IsNullOrWhiteSpace(version) ? SupportedVersion : version)
        && release.Rid == CurrentRid() && release.IsTrusted());

    public static string CurrentRid() => OperatingSystem.IsWindows()
        ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64"
        : OperatingSystem.IsLinux()
            ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64"
            : "unsupported";
}

public sealed record MihomoRuntimeRelease(string Version, string Rid, string AssetName, string ArchiveFormat, string Sha256)
{
    public Uri DownloadUri => new($"https://github.com/MetaCubeX/mihomo/releases/download/{Version}/{AssetName}");
    public bool IsTrusted() => Version == MihomoRuntimeManifest.SupportedVersion
        && (Rid is "win-x64" or "win-arm64" or "linux-x64" or "linux-arm64")
        && (ArchiveFormat is "zip" or "gz")
        && AssetName.Length is > 0 and <= 180 && !AssetName.Contains('/') && !AssetName.Contains('\\')
        && Sha256.Length == 64 && Sha256.All(Uri.IsHexDigit)
        && DownloadUri.Scheme == Uri.UriSchemeHttps && DownloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && DownloadUri.AbsolutePath == $"/MetaCubeX/mihomo/releases/download/{Version}/{AssetName}";
}
