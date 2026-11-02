using System.Runtime.InteropServices;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>Mechanisms delegate to domain code; declarations cannot supply commands, URLs or package names.</summary>
public interface IComponentInstallerProvider
{
    string Type { get; }
    bool CanHandle(ComponentInstallTarget target, IComponentLifecycleAdapter domain);
    Task<ComponentPlanResult> PlanAsync(IComponentLifecycleAdapter domain, ComponentPlanRequest request, CancellationToken ct);
    Task<ComponentExecutionResult> ExecuteAsync(IComponentLifecycleAdapter domain, ComponentExecuteRequest frozen, CancellationToken ct);
}

public sealed class DomainComponentInstallerProvider(string type, ComponentSourceMode mode) : IComponentInstallerProvider
{
    public string Type { get; } = type;
    public bool CanHandle(ComponentInstallTarget target, IComponentLifecycleAdapter domain) =>
        target.Installer.Type == Type && domain.Modes.Any(support => support.Mode == mode
            && support.Platform == target.Os && support.Available);
    public async Task<ComponentPlanResult> PlanAsync(IComponentLifecycleAdapter domain, ComponentPlanRequest request, CancellationToken ct)
    {
        var plan = await domain.PlanAsync(request, ct);
        if (Type != "apt" || !plan.Allowed || plan.Target?.Package is not { } package
            || request.Change is not (RuntimeChange.Install or RuntimeChange.Update)) return plan;
        // Consume the domain's already-read candidate, not a second query for a newer candidate.
        var manifest = BundledComponentCatalog.Current.Components.Single(component => component.Id == domain.ComponentId);
        var host = new ComponentHostEnvironment(ComponentPlatforms.Name, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        var target = manifest.Targets.Single(value => value.Os == host.Os && value.Architectures.Contains(host.Architecture)
            && value.Installer.Type == Type);
        var sources = new ComponentReleaseProviderRegistry([new AptComponentReleaseProvider((_, _) =>
            Task.FromResult(new ComponentPackageResolution(package.PackageName, plan.State?.ActiveVersion, package)))]);
        try
        {
            var releases = await sources.Require(target.Releases.Type).GetReleasesAsync(manifest, target, host, false, ct);
            new PackageArtifactSelector().Select(releases.Single(), target, host);
            return plan; // Preserve the exact original frozen package, dependency and revision objects.
        }
        catch (RuntimePolicyException ex) { return plan with { Problems = [ex.ProblemCode] }; }
    }
    public Task<ComponentExecutionResult> ExecuteAsync(IComponentLifecycleAdapter domain, ComponentExecuteRequest frozen, CancellationToken ct) =>
        domain.ExecuteAsync(frozen, ct);
}

/// <summary>
/// Closed mechanism registry. The desktop .roapp installer is not a Server capability. Unknown
/// mechanisms cannot load plugins or become shell execution. State/recovery remain domain-owned.
/// </summary>
public sealed class ComponentInstallerProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IComponentInstallerProvider> _providers;
    public ComponentInstallerProviderRegistry(IEnumerable<IComponentInstallerProvider> providers) =>
        _providers = providers.ToDictionary(provider => provider.Type, StringComparer.Ordinal);
    public static ComponentInstallerProviderRegistry Host { get; } = new([
        new DomainComponentInstallerProvider("apt", ComponentSourceMode.SystemPackage),
        new DomainComponentInstallerProvider("archive", ComponentSourceMode.ManagedArchive),
        new DomainComponentInstallerProvider("download", ComponentSourceMode.NativeInstaller)
    ]);

    public IComponentInstallerProvider? Find(string type) => _providers.GetValueOrDefault(type);

    public (IComponentInstallerProvider? Provider, string? Problem) Resolve(IComponentLifecycleAdapter domain,
        ComponentPlanRequest request, ResolvedComponentCatalog catalog, string os, string architecture)
    {
        if (request.ComponentId != domain.ComponentId) return (null, ComponentProblemCodes.RequestInvalid);
        var type = request.Mode switch
        {
            ComponentSourceMode.SystemPackage => "apt",
            ComponentSourceMode.ManagedArchive => "archive",
            ComponentSourceMode.NativeInstaller => "download",
            _ => ""
        };
        // Removing or repairing an installation uses its persisted domain facts, even if the
        // catalog no longer lists it. This never grants an action the domain does not support.
        if (request.Change is not (RuntimeChange.Install or RuntimeChange.Update))
            return (Find(type), Find(type) is null ? ComponentProblemCodes.ExecutionUnavailable : null);
        var manifest = catalog.Components.SingleOrDefault(component => component.Id == domain.ComponentId);
        if (manifest is null) return (null, ComponentProblemCodes.UnknownComponent);
        var result = CompatibilityResolver.Resolve(manifest, os, architecture,
            target => target.Installer.Type == type && Find(type)?.CanHandle(target, domain) == true);
        return result.Status == ComponentCompatibility.Supported ? (Find(type), null)
            : (null, result.Status switch
            {
                ComponentCompatibility.UnsupportedPlatform => ComponentProblemCodes.PlatformUnsupported,
                ComponentCompatibility.Unknown => ComponentProblemCodes.StateUnavailable,
                _ => ComponentProblemCodes.ExecutionUnavailable
            });
    }

    internal (IComponentInstallerProvider? Provider, string? Problem) Resolve(IComponentLifecycleAdapter domain, ComponentPlanRequest request) =>
        Resolve(domain, request, request.Change is RuntimeChange.Install or RuntimeChange.Update
                ? BundledComponentCatalog.Current : new ResolvedComponentCatalog([], []), ComponentPlatforms.Name,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
}
