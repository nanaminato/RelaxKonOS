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
    public const string ServicePackage = "postgresql-common";
    public const string ScriptPackage = "postgresql-client-common";
    public const string CommonDevPackage = "postgresql-common-dev";
    public const string ServiceTemplate = "/usr/lib/systemd/system/postgresql@.service";

    public static string? CheckHost(PostgreSqlHostObservation host)
    {
        if (host.Os != "linux" || PostgreSqlPlatform.Match(host.Distribution, host.Version) is null
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

    /// <summary>Reuses the existing SHA-256 module-resolution parser without extending executable package mappings.</summary>
    public static bool TryResolve(PostgreSqlPlatform platform, string policyText, string artifactText,
        out ModulePackageResolution? resolution, out string? problem)
    {
        resolution = null;
        problem = "database.package_unresolved";
        if (policyText.Length > 262144 || artifactText.Length > 262144
            || !AptPackageResolution.TryParsePolicy(policyText, platform.Package, out var policy)
            || policy?.CandidateVersion is not { } version) return false;
        if (policy.InstalledVersion is not null) { problem = "database.external_installation"; return false; }
        var installedLines = policyText.Split('\n').Where(line => line.StartsWith("  Installed:", StringComparison.Ordinal)).ToArray();
        var candidateLines = policyText.Split('\n').Count(line => line.StartsWith("  Candidate:", StringComparison.Ordinal));
        if (installedLines.Length != 1 || candidateLines != 1
            || installedLines[0].Trim() != "Installed: (none)")
        { problem = "database.inventory_unknown"; return false; }
        return TryResolveArtifacts(platform, policy, artifactText, out resolution, out problem);
    }

    public static bool TryResolveArtifacts(PostgreSqlPlatform platform, AptPolicy policy, string artifactText,
        out ModulePackageResolution? resolution, out string? problem)
    {
        resolution = null;
        problem = "database.package_unresolved";
        if (policy.CandidateVersion is not { } version || artifactText.Length > 262144) return false;
        if (!platform.SupportsPackageVersion(version)) { problem = "database.version_unsupported"; return false; }
        if (!AptPackageResolution.TryParseUriPlan(artifactText, platform.Package, version, out var main, out var dependencies)
            || !AptPackageResolution.TryBuildModuleResolution(policy, main, dependencies, [platform.Package], out var target, out _)
            || target is null) return false;
        problem = CheckTarget(platform, target);
        if (problem is not null) return false;
        resolution = target;
        return true;
    }

    public static string? CheckTarget(PostgreSqlPlatform platform, ModulePackageResolution target)
    {
        if (!AptPackageResolution.IsUsableModuleResolution(target, out _)) return "database.package_unresolved";
        if (target.Package != platform.Package || !platform.SupportsPackageVersion(target.Version)) return "database.version_unsupported";
        if (target.Architecture != "amd64") return "database.platform_unsupported";
        if (target.RepositoryId != string.Join('|', target.BaseUri, target.Suite, target.Area, target.Architecture))
            return "database.source_unsupported";
        if (target.Area != "main" || platform.RepositorySigningKeys(target.BaseUri, target.Suite) is null
            || !platform.OfficialArtifact(target.PoolUri)
            || !target.PoolUri.StartsWith(target.BaseUri.TrimEnd('/') + "/pool/", StringComparison.Ordinal)
            || target.Dependencies.Any(item => !platform.OfficialArtifact(item.Uri)))
            return "database.source_unsupported";
        var name = Uri.UnescapeDataString(new Uri(target.PoolUri).Segments[^1]);
        if (name != $"{platform.Package}_{target.Version}_amd64.deb") return "database.package_unresolved";
        var packages = target.Dependencies.Select(item => item.FileName.Split('_')[0]).ToArray();
        if (packages.Contains(platform.Package, StringComparer.Ordinal)
            || packages.Distinct(StringComparer.Ordinal).Count() != packages.Length)
            return "database.package_dependencies_unsupported";
        var client = $"{platform.ClientPackage}_{target.Version}_amd64.deb";
        if (!target.Dependencies.Any(item => item.FileName == client)
            || !target.Dependencies.Any(item => platform.IsCommonPackage(item.FileName, ServicePackage))
            || !target.Dependencies.Any(item => platform.IsCommonPackage(item.FileName, ScriptPackage)))
            return "database.package_dependencies_incomplete";
        // A dependency cannot smuggle in another database major or extension during bootstrap.
        if (target.Dependencies.Any(item => item.FileName.StartsWith("postgresql-", StringComparison.Ordinal)
            && item.FileName != client
            && !platform.IsCommonPackage(item.FileName, ServicePackage)
            && !platform.IsCommonPackage(item.FileName, ScriptPackage)
            && !(platform == PostgreSqlPlatform.Trixie && platform.IsCommonPackage(item.FileName, CommonDevPackage))))
            return "database.package_dependencies_unsupported";
        return null;
    }

}
