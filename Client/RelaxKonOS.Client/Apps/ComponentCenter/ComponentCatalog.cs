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
public enum ComponentSourceKind { ManagedArchive, SystemPackage, OfficialSource }

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
    IReadOnlyList<ComponentSourceDeclaration> Sources);

public sealed record ComponentWorkloadDefinition(string WorkloadId, string DisplayNameKey,
    IReadOnlyList<string> RequiredComponents, IReadOnlyList<string> OptionalComponents);

public static class ComponentCatalog
{
    private const string Linux = "linux";
    private const string Windows = "windows";

    /// <summary>Only the first two entries are backed by a Host runtime in this build.</summary>
    public static IReadOnlyList<HostComponentDefinition> Components { get; } = Array.AsReadOnly<HostComponentDefinition>([
        new("sing-box", "sing-box", "network", "sing-box", "component_center.component.sing-box.description",
            ComponentIntegration.Batch, "relaxkonos.proxy-server", [], [], [],
            ["relaxkonos.proxy-server"], [Windows, Linux],
            [new(ComponentSourceKind.ManagedArchive, true, [Windows, Linux])]),
        new("mihomo", "mihomo", "network", "mihomo", "component_center.component.mihomo.description",
            ComponentIntegration.Runtime, "relaxkonos.proxy", [], [], [],
            ["relaxkonos.proxy"], [Windows, Linux],
            [new(ComponentSourceKind.ManagedArchive, true, [Windows, Linux])]),
        new("frpc", "frpc", "network", "frpc", "component_center.component.frpc.description",
            ComponentIntegration.Installation, "relaxkonos.tunnels", [], [], [],
            ["relaxkonos.tunnels"], [Windows, Linux],
            [new(ComponentSourceKind.ManagedArchive, true, [Windows, Linux]), new(ComponentSourceKind.OfficialSource, false, [Windows, Linux])]),
        new("frps", "frps", "network", "frps", "component_center.component.frps.description",
            ComponentIntegration.Installation, "relaxkonos.tunnels", [], [], [],
            ["relaxkonos.tunnels"], [Windows, Linux],
            [new(ComponentSourceKind.ManagedArchive, true, [Windows, Linux]), new(ComponentSourceKind.OfficialSource, false, [Windows, Linux])]),
        // Linux installs the distribution package behind nginx.service; Windows has no such package
        // and uses a pinned archive. Both platform adapters use durable installation workers.
        new("nginx", "nginx", "web", "Nginx", "component_center.component.nginx.description",
            ComponentIntegration.Installation, "relaxkonos.webservers", [], [], [],
            ["relaxkonos.webservers"], [Linux, Windows],
            [
                new(ComponentSourceKind.SystemPackage, true, [Linux]),
                new(ComponentSourceKind.OfficialSource, false, [Linux]),
                new(ComponentSourceKind.ManagedArchive, true, [Windows])
            ]),
        new("squid", "squid", "web", "Squid", "component_center.component.squid.description",
            ComponentIntegration.Installation, "relaxkonos.squid", [], [], [],
            ["relaxkonos.squid"], [Linux],
            [new(ComponentSourceKind.SystemPackage, true, [Linux])]),
        new("docker", "docker", "system", "Docker Engine", "component_center.component.docker.description",
            ComponentIntegration.Installation, "relaxkonos.docker", [], [], [],
            ["relaxkonos.docker"], [Linux],
            [new(ComponentSourceKind.OfficialSource, false, [Linux]), new(ComponentSourceKind.SystemPackage, true, [Linux])]),
        new("git", "git", "system", "Git", "component_center.component.git.description",
            ComponentIntegration.Installation, "relaxkonos.git", [], [], [],
            ["relaxkonos.git"], [Linux, Windows],
            [new(ComponentSourceKind.SystemPackage, true, [Linux]), new(ComponentSourceKind.OfficialSource, false, [Windows])]),
        new("samba", "samba", "storage", "Samba", "component_center.component.samba.description",
            ComponentIntegration.Installation, "relaxkonos.file-services", [], [], [],
            ["relaxkonos.file-services"], [Linux],
            [new(ComponentSourceKind.SystemPackage, true, [Linux])])
    ]);
    public static IReadOnlyList<ComponentWorkloadDefinition> Workloads { get; } = Array.AsReadOnly<ComponentWorkloadDefinition>([
        new("proxy-server", "component_center.workload.proxy_server", ["sing-box"], []),
        new("proxy-manager", "component_center.workload.proxy_manager", ["mihomo"], [])
    ]);
    public static HostComponentDefinition? Find(string id) => Components.SingleOrDefault(c => c.ComponentId == id);

    /// <summary>
    /// The one component whose availability adapter gates application launch today. A catalog entry
    /// without an adapter must never block its application, because it cannot prove anything about it.
    /// </summary>
    public static HostComponentDefinition? LaunchGate(string? applicationId) => applicationId is null
        ? null : Components.FirstOrDefault(component =>
            component.Integration == ComponentIntegration.Batch && component.Applications.Contains(applicationId));
}

/// <summary>User intent only. This is neither a frozen plan nor an approval.</summary>
public sealed record ComponentChangeDraft(string ComponentId, string RuntimeId, string HostId,
    Guid InstallationId, string? SourceId, string? ReleaseId, string? Version, RuntimeChange Change,
    bool KeepData, bool AllowPrerelease);
