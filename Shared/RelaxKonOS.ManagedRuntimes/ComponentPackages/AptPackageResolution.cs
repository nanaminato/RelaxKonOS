using System.Globalization;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>One repository that offers one version of a package, exactly as apt reported it.</summary>
public sealed record AptRepositoryLine(string Version, long Priority, string BaseUri, string Suite, string Area, string Architecture);

/// <summary>
/// The host package manager's own resolution for one package. <paramref name="InstalledVersion"/>
/// and <paramref name="CandidateVersion"/> come from apt, never from a caller, and
/// <paramref name="Repositories"/> names the repositories that published each version.
/// </summary>
public sealed record AptPolicy(string PackageName, string? InstalledVersion, string? CandidateVersion, IReadOnlyList<AptRepositoryLine> Repositories);

/// <summary>One downloadable artifact as <c>apt-get --print-uris</c> printed it.</summary>
public sealed record AptUriLine(string FileName, string Uri, long Bytes, string Sha256);

/// <summary>
/// Parses the two read-only answers apt gives about a package: which version it would install and
/// from where, and which exact artifacts that transaction would fetch. Both parsers take text so
/// they can be checked without a Linux host; nothing here downloads, installs, or changes a source.
/// </summary>
public static class AptPackageResolution
{
    /// <summary>The only package this build may resolve. A different product needs its own adapter.</summary>
    public const string PackageName = "nginx";

    private static readonly Regex VersionLine = new(@"^\s*(?:\*\*\*\s+)?(?<version>[0-9][0-9A-Za-z.+:~\-]{0,127})\s+(?<priority>\d{1,9})\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryLine = new(@"^\s+(?<priority>\d{1,9})\s+(?<uri>https?://\S+)\s+(?<suite>[^/\s]+)/(?<area>\S+)\s+(?<arch>[A-Za-z0-9\-]+)\s+\S+\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex UriLine = new(@"^'(?<uri>[^']+)'(?:\s+(?<file>\S+))?(?:\s+(?<size>\d{1,15}))?(?:\s+SHA256:(?<sha>[0-9a-fA-F]{64}))?\s*$",
        RegexOptions.CultureInvariant);

    public static bool TryParsePolicy(string? output, string packageName, out AptPolicy? policy)
    {
        policy = null;
        if (string.IsNullOrWhiteSpace(output) || !RuntimeValidation.Identifier(packageName)) return false;
        string? installed = null, candidate = null;
        var repositories = new List<AptRepositoryLine>();
        var seenHeader = false;
        string? pendingVersion = null;
        long pendingPriority = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!seenHeader)
            {
                if (!string.Equals(line.TrimEnd(), packageName + ":", StringComparison.Ordinal)) continue;
                seenHeader = true;
                continue;
            }
            if (line.StartsWith("  Installed:", StringComparison.Ordinal)) { installed = VersionValue(line["  Installed:".Length..]); continue; }
            if (line.StartsWith("  Candidate:", StringComparison.Ordinal)) { candidate = VersionValue(line["  Candidate:".Length..]); continue; }
            var repository = RepositoryLine.Match(line);
            if (repository.Success)
            {
                if (pendingVersion is not null)
                    repositories.Add(new(pendingVersion, pendingPriority, repository.Groups["uri"].Value,
                        repository.Groups["suite"].Value, repository.Groups["area"].Value, repository.Groups["arch"].Value));
                continue;
            }
            var version = VersionLine.Match(line);
            if (!version.Success) { pendingVersion = null; continue; }
            pendingVersion = version.Groups["version"].Value;
            pendingPriority = long.Parse(version.Groups["priority"].Value, CultureInfo.InvariantCulture);
        }
        if (!seenHeader) return false;
        policy = new(packageName, installed, candidate, repositories);
        return true;
    }

    private static string? VersionValue(string text)
    {
        var value = text.Trim();
        var separator = value.IndexOf(" (", StringComparison.Ordinal);
        if (separator >= 0) value = value[..separator];
        return value is "" or "(none)" or "unknown" ? null : value;
    }

    /// <summary>
    /// Reads the transaction apt would run for one pinned version. <paramref name="main"/> is the
    /// package itself; <paramref name="others"/> is everything else the same transaction resolves.
    /// </summary>
    public static bool TryParseUriPlan(string? output, string packageName, string version,
        out AptUriLine? main, out IReadOnlyList<AptUriLine> others)
    {
        main = null;
        others = [];
        if (string.IsNullOrWhiteSpace(output) || !RuntimeValidation.Identifier(packageName)) return false;
        var rest = new List<AptUriLine>();
        foreach (var raw in output.Split('\n'))
        {
            var match = UriLine.Match(raw.TrimEnd('\r'));
            if (!match.Success)
            {
                if (raw.TrimStart().StartsWith("'", StringComparison.Ordinal)) return false;
                continue;
            }
            var uri = match.Groups["uri"].Value;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
                return false;
            var name = Uri.UnescapeDataString(match.Groups["file"].Success ? match.Groups["file"].Value : parsed.Segments[^1]);
            if (name.Length == 0) continue;
            if (main is null && name.StartsWith(packageName + "_", StringComparison.Ordinal)
                && name.Contains("_" + version + "_", StringComparison.Ordinal)
                && long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
                && match.Groups["sha"].Success)
            {
                main = new(name, uri, bytes, match.Groups["sha"].Value.ToLowerInvariant());
                continue;
            }
            if (!long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var dependencyBytes)
                || dependencyBytes is <= 0 or > 512L * 1024 * 1024 || !match.Groups["sha"].Success)
                return false;
            rest.Add(new(name, uri, dependencyBytes, match.Groups["sha"].Value.ToLowerInvariant()));
            if (rest.Count > 64) return false;
        }
        others = rest;
        return main is not null;
    }

    /// <summary>
    /// Joins the two answers into one frozen identity. It refuses when apt named no repository for
    /// the candidate: a digest without a publishing repository is not a frozen source.
    /// </summary>
    public static bool TryBuildTarget(AptPolicy? policy, AptUriLine? main, IReadOnlyList<AptUriLine>? others,
        out ComponentPackageTarget? target, out string? problemCode)
    {
        target = null;
        problemCode = null;
        if (policy is null || main is null) { problemCode = ComponentProblemCodes.PackageUnresolved; return false; }
        var version = policy.CandidateVersion;
        if (version is null || !Uri.UnescapeDataString(main.FileName).Contains("_" + version + "_", StringComparison.Ordinal))
        { problemCode = ComponentProblemCodes.PackageVersionMismatch; return false; }
        var repository = policy.Repositories
            .Where(item => string.Equals(item.Version, version, StringComparison.Ordinal)
                && main.Uri.StartsWith(item.BaseUri.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(item => item.Priority)
            .FirstOrDefault();
        if (repository is null) { problemCode = ComponentProblemCodes.PackageIndexUnavailable; return false; }
        if (!SafeRepository(repository) || main.Bytes is <= 0 or > 512L * 1024 * 1024
            || main.Sha256.Length != 64 || !main.Sha256.All(Uri.IsHexDigit))
        { problemCode = ComponentProblemCodes.PackageUnresolved; return false; }
        var dependencies = (others ?? []).ToArray();
        if (dependencies.Length > 64 || dependencies.Any(item => item.FileName == main.FileName) || dependencies.Select(item => item.FileName).Distinct(StringComparer.Ordinal).Count() != dependencies.Length
            || dependencies.Any(item => item.FileName.Length is <= 0 or > 256
                || !item.FileName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+' or ':')
                || item.Bytes is <= 0 or > 512L * 1024 * 1024 || item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit)))
        { problemCode = ComponentProblemCodes.PackageUnresolved; return false; }
        var alsoInstalls = dependencies.Select(item => new ComponentPackageArtifact(item.FileName, item.Uri, item.Bytes, item.Sha256)).ToArray();
        target = new(policy.PackageName, version, repository.Architecture,
            RepositoryId(repository), repository.BaseUri, repository.Suite, repository.Area,
            main.Uri, main.Sha256, main.Bytes, alsoInstalls);
        if (!IsUsableExpectation(target)) { target = null; problemCode = ComponentProblemCodes.PackageUnresolved; return false; }
        return true;
    }

    public static string RepositoryId(AptRepositoryLine repository) =>
        string.Join('|', repository.BaseUri, repository.Suite, repository.Area, repository.Architecture);

    /// <summary>
    /// True only when a fresh resolution is still the identity a caller froze. Version, repository
    /// and digest must all agree: matching only the version would let a republished or mirrored
    /// package install under an identity nobody reviewed.
    /// </summary>
    public static bool MatchesExpectation(ComponentPackageTarget? expected, ComponentPackageTarget? current) =>
        expected is not null && current is not null
        && string.Equals(expected.PackageName, current.PackageName, StringComparison.Ordinal)
        && string.Equals(expected.Version, current.Version, StringComparison.Ordinal)
        && string.Equals(expected.Architecture, current.Architecture, StringComparison.Ordinal)
        && string.Equals(expected.RepositoryId, current.RepositoryId, StringComparison.Ordinal)
        && string.Equals(expected.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase)
        && expected.Bytes == current.Bytes
        && string.Equals(expected.BaseUri, current.BaseUri, StringComparison.Ordinal)
        && string.Equals(expected.Suite, current.Suite, StringComparison.Ordinal)
        && string.Equals(expected.Area, current.Area, StringComparison.Ordinal)
        && string.Equals(expected.PoolUri, current.PoolUri, StringComparison.Ordinal)
        && expected.AlsoInstalls is not null && current.AlsoInstalls is not null
        && expected.AlsoInstalls.OrderBy(item => item.FileName, StringComparer.Ordinal)
            .SequenceEqual(current.AlsoInstalls.OrderBy(item => item.FileName, StringComparer.Ordinal));

    /// <summary>A frozen identity that names another package, a malformed digest or no repository is unusable.</summary>
    public static bool IsUsableExpectation(ComponentPackageTarget? target) => target is not null
        && HostPackageDefinitions.KnownPackage(target.PackageName)
        && target.Version is { Length: > 0 and <= 128 }
        && target.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '~' or ':' or '-')
        && target.Architecture is { Length: > 0 and <= 32 }
        && target.Sha256 is { Length: 64 } && target.Sha256.All(Uri.IsHexDigit)
        && target.Bytes is > 0 and <= 512L * 1024 * 1024
        && target.RepositoryId is not null && target.RepositoryId.Contains('|')
        && target.RepositoryId.Length <= 4096 && target.PoolUri is { Length: > 0 and <= 8192 }
        && target.BaseUri is { Length: > 0 and <= 8192 }
        && target.AlsoInstalls is { Count: <= 64 } && target.AlsoInstalls.All(item => item is not null && !string.IsNullOrWhiteSpace(item.FileName)
            && item.FileName.Length <= 256 && item.Uri is { Length: > 0 and <= 8192 }
            && Uri.TryCreate(item.Uri, UriKind.Absolute, out var dependencyUri) && dependencyUri.Scheme is "http" or "https"
            && item.Bytes is > 0 and <= 512L * 1024 * 1024 && item.Sha256 is { Length: 64 } && item.Sha256.All(Uri.IsHexDigit))
        && Uri.TryCreate(target.BaseUri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(target).Length <= 196608;

    private static bool SafeRepository(AptRepositoryLine repository) =>
        Uri.TryCreate(repository.BaseUri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && SafeToken(repository.Suite) && SafeToken(repository.Area)
        && repository.Architecture.Length is > 0 and <= 32
        && repository.Architecture.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool SafeToken(string text) => text.Length is > 0 and <= 64
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
