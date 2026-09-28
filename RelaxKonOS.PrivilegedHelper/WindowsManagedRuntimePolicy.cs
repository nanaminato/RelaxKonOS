using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Machine-admin configuration, never supplied by a remote request.</summary>
public sealed record WindowsManagedRuntimePolicy(string NginxRoot, string PrivateRoot,
    IReadOnlyList<string> ArchiveRoots, IReadOnlyList<string> ReaderSids,
    IReadOnlyList<WindowsFrpRelease> FrpReleases)
{
    public static WindowsManagedRuntimePolicy Create(string? nginxRoot, string? privateRoot,
        IReadOnlyList<string>? archiveRoots, IReadOnlyList<string> readers, IReadOnlyList<WindowsFrpRelease>? releases)
        => new(Path.GetFullPath(nginxRoot ?? WindowsManagedRuntimeDefaults.NginxRoot),
            Path.GetFullPath(privateRoot ?? WindowsManagedRuntimeDefaults.PrivateRoot),
            archiveRoots ?? [], readers, releases ?? WindowsManagedRuntimeDefaults.FrpReleases);

    public void Validate()
    {
        foreach (var root in new[] { NginxRoot, PrivateRoot }.Concat(ArchiveRoots))
            if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root).TrimEnd('\\', '/') == Path.GetPathRoot(root)?.TrimEnd('\\', '/'))
                throw new InvalidOperationException("Runtime roots must be absolute directories below a volume root.");
        var nginxParent = Path.GetDirectoryName(Path.GetFullPath(NginxRoot).TrimEnd('\\', '/'))!;
        if (nginxParent.TrimEnd('\\', '/') == Path.GetPathRoot(nginxParent)?.TrimEnd('\\', '/')
            || ArchiveRoots.Any(root => Contains(nginxParent, root) || Contains(root, nginxParent))
            || Contains(nginxParent, PrivateRoot))
            throw new InvalidOperationException("The Nginx parent must be a dedicated directory separate from ingress and other runtimes.");
        if (Contains(NginxRoot, PrivateRoot) || Contains(PrivateRoot, NginxRoot))
            throw new InvalidOperationException("Nginx and private runtime roots must be separate.");
        if (ArchiveRoots.Any(root => Contains(root, NginxRoot) || Contains(root, PrivateRoot)
            || Contains(NginxRoot, root) || Contains(PrivateRoot, root)))
            throw new InvalidOperationException("Archive ingress must be separate from protected runtimes.");
        if (FrpReleases.GroupBy(x => (x.Version, x.Rid)).Any(x => x.Count() != 1)
            || FrpReleases.Any(x => !IsTrustedRelease(x)))
            throw new InvalidOperationException("Windows FRP release pins are invalid.");
    }

    public static string Rid => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    public static bool IsTrustedRelease(WindowsFrpRelease release) =>
        Regex.IsMatch(release.Version, "^v[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)
        && release.Rid is "win-x64" or "win-arm64" && release.ArchiveFormat == "zip"
        && Regex.IsMatch(release.Sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)
        && release.Url == $"https://github.com/fatedier/frp/releases/download/{release.Version}/frp_{release.Version[1..]}_windows_{(release.Rid == "win-x64" ? "amd64" : "arm64")}.zip";

    public static bool Contains(string root, string path)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var canonicalPath = Path.GetFullPath(path);
        return canonicalPath.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void RequireNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Runtime paths cannot contain reparse points.");
    }
}
