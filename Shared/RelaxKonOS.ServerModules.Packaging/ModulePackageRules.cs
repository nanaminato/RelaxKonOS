using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace RelaxKonOS.ModulePackaging;

internal static partial class ModulePackageRules
{
    internal static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new ModulePackageException("module-package-" + code); }
    [GeneratedRegex("^[a-z][a-z0-9.-]{0,95}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
    internal static bool Id(string? value) => value is not null && IdentifierPattern().IsMatch(value);
    internal static bool Digest(string? value) => value is not null && DigestPattern().IsMatch(value);
    internal static Version Version(string? value)
    {
        Require(value is not null && System.Version.TryParse(value, out var parsed)
            && parsed.Build >= 0 && parsed.Revision == -1 && parsed.ToString(3) == value, "version-invalid");
        return System.Version.Parse(value!);
    }
    internal static bool Path(string? value) => value is { Length: > 0 and <= 240 }
        && value.Split('/').All(part => part.Length is > 0 and <= 100 && part != "." && part != ".."
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        && !value.EndsWith('.') && !value.Split('/').Any(p => p.EndsWith('.'))
        && !value.Split('/').Any(p => Reserved(p.Split('.')[0]));
    private static bool Reserved(string value) => value.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
        or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    internal static void Manifest(ModulePackageManifest m)
    {
        Require(m.SchemaVersion == 1 && m.PackageKind == "server-module" && Id(m.ModuleId) && Id(m.ReleaseId)
            && m.ModuleId is not ("core" or "relaxkonos.core")
            && Id(m.PublisherId) && m.Purpose is "official" or "development"
            && m.Rid is "linux-x64" or "linux-arm64" or "win-x64" or "win-arm64" or "osx-x64" or "osx-arm64",
            "manifest-invalid");
        _ = Version(m.Version);
        Require(Version(m.HostApiMinimum) < Version(m.HostApiMaximumExclusive), "host-api-range-invalid");
        Require(Id(m.BuildId) && m.SourceCommit is { Length: 40 or 64 } && m.SourceCommit.All(c => char.IsAsciiHexDigitLower(c)), "build-identity-invalid");
        Require(m.EntryType is { Length: > 0 and <= 256 } && m.EntryType.Split('.').All(p => p.Length > 0
            && (char.IsAsciiLetter(p[0]) || p[0] == '_') && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')), "entry-type-invalid");
        Require(m.Dependencies is { Length: <= 128 } && m.Capabilities is { Length: <= 128 }
            && m.Files is { Length: > 0 and <= 4096 }, "manifest-limits");
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in m.Dependencies!)
            Require(dependency is not null && Id(dependency.ModuleId) && dependency.ModuleId != m.ModuleId
                && dependencies.Add(dependency.ModuleId) && Version(dependency.MinimumVersion) < Version(dependency.MaximumVersionExclusive), "dependency-invalid");
        Require(m.Capabilities!.All(Id) && m.Capabilities.Distinct(StringComparer.Ordinal).Count() == m.Capabilities.Length, "capability-invalid");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in m.Files!)
        {
            Require(file is not null && Path(file.Path) && file.Path.StartsWith("server/" + m.Rid + "/", StringComparison.Ordinal)
                && paths.Add(file.Path) && Digest(file.Sha256) && file.Length is >= 0 and <= 268435456, "file-invalid");
            total += file!.Length;
        }
        Require(total <= 1073741824 && Path(m.EntryAssembly) && m.EntryAssembly.EndsWith(".dll", StringComparison.Ordinal)
            && m.Files.Any(f => f.Path == m.EntryAssembly), "entry-assembly-invalid");
        // A file cannot also be a directory, including on case-insensitive hosts.
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            { Require(!paths.Contains(path[..slash]), "file-directory-collision"); slash = path.LastIndexOf('/', slash - 1); }
        }
    }
}
