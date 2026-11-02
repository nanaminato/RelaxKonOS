using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// How far a component lifecycle is really connected to this workspace. The catalog describes the
/// product intent; only a Host adapter can grant an executable capability, so an
/// <see cref="ComponentIntegration.Application"/> entry is a pointer to the owning application and
/// never an install button.
/// </summary>
public enum ComponentIntegration
{
    /// <summary>Draft, frozen TaskPlan and the domain runtime page are all available (sing-box).</summary>
    Batch,
    /// <summary>The domain runtime page is available, but no batch TaskPlan path exists yet (mihomo).</summary>
    Runtime,
    /// <summary>Installation still belongs to the owning application; the center only lists the intent.</summary>
    Application,
    /// <summary>Platform package freeze and durable first-install worker; not a batch runtime.</summary>
    Installation
}

/// <summary>Declared install provenance. A declaration is a plan, not a granted or verified source.</summary>
public enum ComponentSourceKind { ManagedArchive, SystemPackage, OfficialSource, NativeInstaller, DesktopApp }

/// <summary>
/// One declared source for one set of platforms. Linux and Windows genuinely differ here: a
/// distribution package is only a source where that package manager exists, while the managed
/// archive is the Windows path for the same product. <paramref name="AdapterAvailable"/> stays false
/// until the Host adapter and its trust rules exist for that platform.
/// </summary>
public sealed record ComponentSourceDeclaration(ComponentSourceKind Kind, bool AdapterAvailable, IReadOnlyList<string> Platforms);

/// <summary>A display-only chip: platform, declared source name, and whether its adapter exists.</summary>
public sealed record ComponentSourceChip(LocalizedStatus Text, LocalizedStatus Platform, bool Ready, bool IsHostPlatform);

public sealed record HostComponentDefinition(string ComponentId, string RuntimeId, string Category,
    string DisplayName, string DescriptionKey, ComponentIntegration Integration, string? ApplicationId,
    IReadOnlyList<string> RequiredComponents, IReadOnlyList<string> OptionalComponents,
    IReadOnlyList<string> Conflicts, IReadOnlyList<string> Applications, IReadOnlyList<string> Platforms,
    IReadOnlyList<ComponentSourceDeclaration> Sources, bool IsDesktop);

public sealed record ComponentWorkloadDefinition(string WorkloadId, string DisplayNameKey,
    IReadOnlyList<string> RequiredComponents, IReadOnlyList<string> OptionalComponents);

public static class ComponentCatalog
{
    public static IReadOnlyList<HostComponentDefinition> Components { get; } = Array.AsReadOnly(
        RelaxKonOS.ManagedRuntimes.Catalog.BundledComponentCatalog.Current.Components.Select(component =>
            new HostComponentDefinition(component.Id, component.RuntimeId, component.Category, component.Name,
                component.DescriptionKey, Enum.Parse<ComponentIntegration>(component.Integration), component.ApplicationId,
                component.RequiredComponents, component.OptionalComponents, component.Conflicts, component.Applications,
                component.Targets.Select(target => target.Os).Distinct().ToArray(),
                component.Targets.GroupBy(target => (target.SourceKind, target.Installer.Available))
                    .Select(group => new ComponentSourceDeclaration(Enum.Parse<ComponentSourceKind>(group.Key.SourceKind),
                        group.Key.Available, group.Select(target => target.Os).Distinct().ToArray())).ToArray(),
                component.Targets.All(target => target.Installer.Type == "roapp"))).ToArray());
    public static IReadOnlyList<ComponentWorkloadDefinition> Workloads { get; } = Array.AsReadOnly(
        RelaxKonOS.ManagedRuntimes.Catalog.BundledComponentCatalog.Current.Workloads.Select(workload =>
            new ComponentWorkloadDefinition(workload.WorkloadId, workload.DisplayNameKey,
                workload.RequiredComponents, workload.OptionalComponents)).ToArray());
    public static HostComponentDefinition? Find(string id) => Components.SingleOrDefault(c => c.ComponentId == id);

    /// <summary>
    /// What an application needs before it may open. Every listed component is an <em>alternative</em>:
    /// one installed component satisfies the dependency, because frpc and frps are two ways to use
    /// the same Tunnel Manager. The list comes from each component's own application association, so
    /// an application appears here only because a component declares it, never because a caller
    /// guessed a component id.
    /// </summary>
    public sealed record ApplicationLaunchGate(string ApplicationId, IReadOnlyList<string> ComponentIds);

    /// <summary>
    /// The single source of truth for launch gating. Every catalog component answers through the
    /// generic component state route, so the gate covers exactly the applications the catalog relates
    /// to a component; an application without a component never gains a fake dependency.
    /// </summary>
    public static IReadOnlyList<ApplicationLaunchGate> LaunchGates { get; } = Array.AsReadOnly(
        Components.Where(component => !component.IsDesktop).SelectMany(component => component.Applications.Select(application => (application, component.ComponentId)))
            .GroupBy(pair => pair.application, StringComparer.Ordinal)
            .Select(group => new ApplicationLaunchGate(group.Key,
                Array.AsReadOnly(group.Select(pair => pair.ComponentId).Distinct(StringComparer.Ordinal).ToArray())))
            .OrderBy(gate => gate.ApplicationId, StringComparer.Ordinal)
            .ToArray());

    /// <summary>The dependency an application must satisfy to open; null when it has no component.</summary>
    public static ApplicationLaunchGate? LaunchGate(string? applicationId) => applicationId is null
        ? null : LaunchGates.FirstOrDefault(gate => gate.ApplicationId == applicationId);

    /// <summary>
    /// Components whose owning application resolves the executable itself, so an existing external
    /// copy is already a usable dependency: the host probes <c>git</c> on PATH (Git/HostGitCli) and
    /// never asks the component ledger where it came from. Refusing to open the application until
    /// somebody adopts an installation into the ledger would claim a prerequisite this application
    /// does not actually have. Component Center still reports the ownership and still offers "纳入管理",
    /// because that is what makes update and uninstall possible — bookkeeping, not a precondition.
    /// The set is closed and explicit so an external copy can never satisfy a dependency the
    /// application genuinely needs the ledger for (nginx, docker, an archive runtime).
    /// </summary>
    public static IReadOnlyList<string> ExternalLaunchComponents { get; } = Array.AsReadOnly(["git"]);

    public static bool AllowsExternalLaunch(string componentId) =>
        ExternalLaunchComponents.Contains(componentId, StringComparer.Ordinal);
}

/// <summary>User intent only. This is neither a frozen plan nor an approval.</summary>
public sealed record ComponentChangeDraft(string ComponentId, string RuntimeId, string HostId,
    Guid InstallationId, string? SourceId, string? ReleaseId, string? Version, RuntimeChange Change,
    bool KeepData, bool AllowPrerelease);
