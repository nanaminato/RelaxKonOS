using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.Catalog;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record RoappComponentInstallPlan(string SourceId, string ComponentId, string ReleaseId,
    ComponentHostEnvironment Target, AppPackageCandidate Package);

/// <summary>Stages exact local bytes, then hands the plan to the existing package review/install UI.</summary>
public sealed class RoappComponentInstallerProvider(AppPackageInstallerService installer)
{
    public string Type => "roapp";
    public static ComponentHostEnvironment Desktop => new(OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());

    public async Task<RoappComponentInstallPlan> PlanAsync(LocalCatalogSource source, string componentId, CancellationToken ct = default)
    {
        var component = source.Load().Components.SingleOrDefault(value => value.Id == componentId)
            ?? throw new RuntimePolicyException("component.unknown");
        var resolution = CompatibilityResolver.Resolve(component, Desktop.Os, Desktop.Architecture,
            target => target.Installer.Type == Type);
        if (resolution.Target is not { } target)
            throw new RuntimePolicyException(resolution.Status == ComponentCompatibility.UnsupportedPlatform
                ? "component.platform_unsupported" : "component.execution_unavailable");
        var providers = new ComponentReleaseProviderRegistry([new LocalComponentReleaseProvider(), new LocalComponentReleaseProvider("static")]);
        var releases = await providers.Require(target.Releases.Type).GetReleasesAsync(component, target, Desktop, false, ct);
        var release = releases.Single();
        var artifact = new CatalogArtifactSelector().Select(release, target, Desktop);
        var stagingRoot = Path.Combine(Path.GetTempPath(), "RelaxKonOS", "app-installer");
        Directory.CreateDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N") + ".roapp");
        try
        {
            await using (var input = source.Open(artifact.Reference!, source.ManifestReference(componentId)))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await RelaxKonOS.ManagedRuntimes.ComponentPackages.OfficialComponentReleases.CopyAsync(input, output, artifact.Sha256, ct);
            var candidate = await installer.InspectLocalAsync(staging, source.SourceId + " / " + artifact.Reference,
                deleteWhenFinished: true, cancellationToken: ct);
            if (candidate.Manifest.Id != (component.ApplicationId ?? component.Id) || candidate.Manifest.Version != release.Version)
                throw new RuntimePolicyException("component.package_expectation_changed");
            return new(source.SourceId, component.Id, release.ReleaseId, Desktop,
                candidate with { ExpectedSha256 = artifact.Sha256, CatalogSourceId = source.SourceId });
        }
        catch
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw;
        }
    }
}
