using System.Text.RegularExpressions;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.ManagedRuntimes.PostgreSql;

/// <summary>Host observations, never client claims. Null inventory means unknown, not empty.</summary>
public sealed record PostgreSqlHostObservation(
    string Os, string Distribution, string Version, string Architecture,
    bool SystemdAvailable, bool PackageAuditClean,
    IReadOnlyList<string>? ExistingPackages, IReadOnlyList<string>? ExistingClusters,
    bool? PathsAbsent, bool? PortAvailable);

/// <summary>
/// Pure first-install preflight. A successful result is NOT execution authorization or signature
/// verification. The eventual Host/Helper executor must independently verify signed indexes,
/// freeze its plan and recheck these observations under the package and instance locks.
/// </summary>
public static class PostgreSqlBootstrapPolicy
{
    public const string Package = "postgresql-16";
    public const string MinimumPackageVersion = "16.15-0ubuntu0.24.04.1";
    public const string ServicePackage = "postgresql-common";
    public const string ScriptPackage = "postgresql-client-common";
    public const string ServiceTemplate = "/usr/lib/systemd/system/postgresql@.service";
    public const string Executable = "/usr/lib/postgresql/16/bin/postgres";

    public static string? CheckHost(PostgreSqlHostObservation host)
    {
        if (host.Os != "linux" || host.Distribution != "ubuntu" || host.Version != "24.04"
            || host.Architecture != "amd64") return "database.platform_unsupported";
        if (!host.SystemdAvailable) return "database.service_host_unavailable";
        if (!host.PackageAuditClean) return "database.package_recovery_required";
        if (host.ExistingPackages is null || host.ExistingClusters is null
            || host.PathsAbsent is null || host.PortAvailable is null) return "database.inventory_unknown";
        // First installation only. External installations need a separately designed ownership flow.
        if (host.ExistingPackages.Count != 0 || host.ExistingClusters.Count != 0 || host.PathsAbsent != true)
            return "database.external_installation";
        return host.PortAvailable == true ? null : "database.port_conflict";
    }

    public static bool SupportsPackageVersion(string? version) => version is { Length: <= 128 }
        && Regex.IsMatch(version, @"\A16\.[0-9]+-0ubuntu0\.24\.04\.[0-9]+\z", RegexOptions.CultureInvariant)
        && DebianVersion.Compare(version, MinimumPackageVersion) >= 0;

    /// <summary>Reuses the existing SHA-256 module-resolution parser without extending executable package mappings.</summary>
    public static bool TryResolve(string policyText, string artifactText,
        out ModulePackageResolution? resolution, out string? problem)
    {
        resolution = null;
        problem = "database.package_unresolved";
        if (policyText.Length > 262144 || artifactText.Length > 262144
            || !AptPackageResolution.TryParsePolicy(policyText, Package, out var policy)
            || policy?.CandidateVersion is not { } version) return false;
        if (policy.InstalledVersion is not null) { problem = "database.external_installation"; return false; }
        var installedLines = policyText.Split('\n').Where(line => line.StartsWith("  Installed:", StringComparison.Ordinal)).ToArray();
        var candidateLines = policyText.Split('\n').Count(line => line.StartsWith("  Candidate:", StringComparison.Ordinal));
        if (installedLines.Length != 1 || candidateLines != 1
            || installedLines[0].Trim() != "Installed: (none)")
        { problem = "database.inventory_unknown"; return false; }
        if (!SupportsPackageVersion(version)) { problem = "database.version_unsupported"; return false; }
        if (!AptPackageResolution.TryParseUriPlan(artifactText, Package, version, out var main, out var dependencies)
            || !AptPackageResolution.TryBuildModuleResolution(policy, main, dependencies, [Package], out var target, out _)
            || target is null) return false;
        problem = CheckTarget(target);
        if (problem is not null) return false;
        resolution = target;
        return true;
    }

    public static string? CheckTarget(ModulePackageResolution target)
    {
        if (!AptPackageResolution.IsUsableModuleResolution(target, out _)) return "database.package_unresolved";
        if (target.Package != Package || !SupportsPackageVersion(target.Version)) return "database.version_unsupported";
        if (target.Architecture != "amd64") return "database.platform_unsupported";
        if (target.RepositoryId != string.Join('|', target.BaseUri, target.Suite, target.Area, target.Architecture))
            return "database.source_unsupported";
        if (target.Area != "main" || target.Suite is not ("noble" or "noble-updates" or "noble-security")
            || !OfficialUri(target.BaseUri, artifact: false) || !OfficialUri(target.PoolUri, artifact: true)
            || !target.PoolUri.StartsWith(target.BaseUri.TrimEnd('/') + "/pool/", StringComparison.Ordinal)
            || target.Dependencies.Any(item => !OfficialUri(item.Uri, artifact: true)))
            return "database.source_unsupported";
        var name = Uri.UnescapeDataString(new Uri(target.PoolUri).Segments[^1]);
        if (name != $"{Package}_{target.Version}_amd64.deb") return "database.package_unresolved";
        var packages = target.Dependencies.Select(item => item.FileName.Split('_')[0]).ToArray();
        if (packages.Contains(Package, StringComparer.Ordinal)
            || packages.Distinct(StringComparer.Ordinal).Count() != packages.Length)
            return "database.package_dependencies_unsupported";
        // A fresh-host plan must include a matching client and the two independently owned service/script packages.
        if (!target.Dependencies.Any(item => item.FileName == $"postgresql-client-16_{target.Version}_amd64.deb")
            || !target.Dependencies.Any(item => CommonPackage(item.FileName, ServicePackage))
            || !target.Dependencies.Any(item => CommonPackage(item.FileName, ScriptPackage)))
            return "database.package_dependencies_incomplete";
        // A dependency cannot smuggle in another database major or extension during bootstrap.
        if (target.Dependencies.Any(item => item.FileName.StartsWith("postgresql-", StringComparison.Ordinal)
            && item.FileName != $"postgresql-client-16_{target.Version}_amd64.deb"
            && !CommonPackage(item.FileName, ServicePackage) && !CommonPackage(item.FileName, ScriptPackage)))
            return "database.package_dependencies_unsupported";
        return null;
    }

    private static bool CommonPackage(string file, string package) => Regex.IsMatch(file,
        @"\A" + Regex.Escape(package) + @"_257build1(?:\.[0-9]+)?_all\.deb\z", RegexOptions.CultureInvariant);

    private static bool OfficialUri(string value, bool artifact)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.Host is not ("archive.ubuntu.com" or "security.ubuntu.com" or "cn.archive.ubuntu.com")
            || Regex.IsMatch(value, @"%(?!2[bB]|3[aA])", RegexOptions.CultureInvariant)
            || value.Contains('\\') || value.Contains("/../", StringComparison.Ordinal)) return false;
        return artifact ? uri.AbsolutePath.StartsWith("/ubuntu/pool/", StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith(".deb", StringComparison.Ordinal)
            : uri.AbsolutePath.TrimEnd('/') == "/ubuntu";
    }
}
