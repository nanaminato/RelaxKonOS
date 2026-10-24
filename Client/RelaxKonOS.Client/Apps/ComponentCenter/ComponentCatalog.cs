using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>Compiled display metadata. Executable capabilities always come from the host adapter.</summary>
public sealed record HostComponentDefinition(string ComponentId, string RuntimeId, string Category,
    string DisplayName, IReadOnlyList<string> RequiredComponents, IReadOnlyList<string> OptionalComponents,
    IReadOnlyList<string> Conflicts, IReadOnlyList<string> Applications, IReadOnlyList<string> Platforms);

public sealed record ComponentWorkloadDefinition(string WorkloadId, string DisplayNameKey,
    IReadOnlyList<string> RequiredComponents, IReadOnlyList<string> OptionalComponents);

public static class ComponentCatalog
{
    public static IReadOnlyList<HostComponentDefinition> Components { get; } = Array.AsReadOnly<HostComponentDefinition>([
        new("sing-box", "sing-box", "network", "sing-box", [], [], [],
            ["relaxkonos.proxy-server"], ["windows", "linux"])
    ]);
    public static IReadOnlyList<ComponentWorkloadDefinition> Workloads { get; } = Array.AsReadOnly<ComponentWorkloadDefinition>([
        new("proxy-server", "component_center.workload.proxy_server", ["sing-box"], [])
    ]);
    public static HostComponentDefinition? Find(string id) => Components.SingleOrDefault(c => c.ComponentId == id);
}

/// <summary>User intent only. This is neither a frozen plan nor an approval.</summary>
public sealed record ComponentChangeDraft(string ComponentId, string RuntimeId, string HostId,
    Guid InstallationId, string? SourceId, string? ReleaseId, string? Version, RuntimeChange Change,
    bool KeepData, bool AllowPrerelease);

/// <summary>Kept locally for this desktop process, isolated by server identity and actor.</summary>
public sealed class ComponentDraftStore
{
    private readonly Dictionary<(string Server, Guid Actor), ComponentChangeDraft[]> _drafts = [];
    public IReadOnlyList<ComponentChangeDraft> Read(string server, Guid actor) =>
        _drafts.TryGetValue((server, actor), out var items) ? items.ToArray() : [];
    public void Save(string server, Guid actor, IEnumerable<ComponentChangeDraft> drafts)
    {
        var items = drafts.ToArray();
        if (items.Length == 0) _drafts.Remove((server, actor));
        else _drafts[(server, actor)] = items;
    }
}
