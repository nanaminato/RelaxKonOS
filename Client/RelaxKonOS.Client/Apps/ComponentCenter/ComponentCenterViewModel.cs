using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record ComponentDraftItem(ComponentChangeDraft Draft)
{
    public LocalizedStatus Summary => LocalizedStatus.Format("component_center.draft_summary", Draft.ComponentId,
        LocalizedStatus.Key("proxy_server.runtime.action." + Draft.Change), Draft.Version ?? "—");
}

/// <summary>Selection is local. Only preview enters the host planner and its approval flow.</summary>
public partial class ComponentCenterViewModel : ObservableObject, IDisposable
{
    private readonly ComponentDraftStore _store;
    private readonly string _server;
    private readonly Guid _actor;
    private readonly List<ComponentTreeNode> _componentRoots = [];
    private readonly List<ComponentTreeNode> _workloadRoots = [];
    public ObservableCollection<ComponentCenterNavigationItem> Pages { get; } = new(Enum.GetValues<ComponentCenterPage>().Select(page => new ComponentCenterNavigationItem(page)));
    public ObservableCollection<ComponentTreeNode> VisibleRoots { get; } = [];
    [ObservableProperty] private ComponentCenterNavigationItem? selectedNavigation;
    [ObservableProperty] private ComponentTreeNode? selectedNode;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool? connectionAvailable;
    public LocalizedStatus ConnectionText => LocalizedStatus.Key(ConnectionAvailable switch
    { true => "component_center.connected", false => "component_center.disconnected", _ => "component_center.connection_unknown" });
    partial void OnConnectionAvailableChanged(bool? value) => OnPropertyChanged(nameof(ConnectionText));
    public ComponentCenterPage SelectedPage => SelectedNavigation?.Page ?? ComponentCenterPage.All;
    public bool HasResults => VisibleRoots.Count > 0;
    public bool HasRuntimeDetails => HasResults && SelectedNode?.ComponentId is not null;
    public bool HasWorkloadDetails => HasResults && SelectedNode?.WorkloadId is not null && SelectedNode.ComponentId is null;
    public LocalizedStatus SelectedTitle => SelectedNode?.Label ?? LocalizedStatus.Key("component_center.choose_component");
    public LocalizedStatus EmptyMessage => LocalizedStatus.Key(SelectedPage == ComponentCenterPage.Installed && Runtime.Installation is null
        ? "component_center.observation_unknown" : SelectedPage == ComponentCenterPage.Updates
        ? "component_center.no_updates" : "component_center.no_matches");
    public LocalizedStatus WorkloadRequirements => LocalizedStatus.Literal(SelectedNode?.WorkloadId is { } id
        ? string.Join(", ", ComponentCatalog.Workloads.Single(workload => workload.WorkloadId == id).RequiredComponents) : "");
    public LocalizedStatus WorkloadOptional => LocalizedStatus.Literal(SelectedNode?.WorkloadId is { } id
        ? string.Join(", ", ComponentCatalog.Workloads.Single(workload => workload.WorkloadId == id).OptionalComponents) : "");
    public ProxyServerRuntimeViewModel Runtime { get; }
    public string HostIdentity => _server;
    public ObservableCollection<ComponentDraftItem> Drafts { get; } = [];
    public bool HasDrafts => Drafts.Count > 0;
    public bool CanPreview => HasDrafts && !Runtime.IsBusy && Runtime.CanManage && !Runtime.IsInvalidated && Runtime.Installation is not null;
    public LocalizedStatus DraftSummary => LocalizedStatus.Format("component_center.pending", Drafts.Count);
    [ObservableProperty] private LocalizedStatus notice = LocalizedStatus.Literal("");

    public ComponentCenterViewModel(ProxyServerRuntimeViewModel runtime, ComponentDraftStore store, string server, Guid actor)
    {
        Runtime = runtime; _store = store; _server = server; _actor = actor;
        BuildTrees();
        foreach (var draft in store.Read(server, actor)) Drafts.Add(new(draft));
        SelectedNavigation = Pages[0];
        RefreshTree();
        runtime.StageChange = Stage;
        runtime.PropertyChanged += RuntimeChanged;
        runtime.StateChanged += RuntimeStateChanged;
    }
    private void RuntimeChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(CanPreview));
    private void RuntimeStateChanged(object? sender, EventArgs e) => RefreshTree();
    partial void OnSelectedNavigationChanged(ComponentCenterNavigationItem? value)
    {
        OnPropertyChanged(nameof(SelectedPage));
        SelectedNode = null;
        RefreshTree();
    }
    partial void OnSelectedNodeChanged(ComponentTreeNode? value) => NotifyContext();
    partial void OnSearchChanged(string value) => RefreshTree();
    public void Navigate(ComponentCenterPage page) => SelectedNavigation = Pages.Single(item => item.Page == page);
    public void Locate(ComponentCenterTarget target)
    {
        Search = "";
        Navigate(target.Page);
        SelectedNode = VisibleRoots.SelectMany(root => root.VisibleDescendantsAndSelf()).FirstOrDefault(node =>
            target.WorkloadId is { } workload ? node.WorkloadId == workload && node.ComponentId is null : node.ComponentId == target.ComponentId);
    }
    private void NotifyContext()
    {
        foreach (var property in new[] { nameof(HasResults), nameof(HasRuntimeDetails), nameof(HasWorkloadDetails),
            nameof(SelectedTitle), nameof(EmptyMessage), nameof(WorkloadRequirements), nameof(WorkloadOptional) }) OnPropertyChanged(property);
    }
    private void BuildTrees()
    {
        ComponentTreeNode Leaf(string id, string? workload = null, bool optional = false) => new(
            (workload ?? "component") + ":" + id, LocalizedStatus.Literal(ComponentCatalog.Find(id)!.DisplayName), id, workload, optional, workload is not null);
        foreach (var group in ComponentCatalog.Components.GroupBy(component => component.Category))
        {
            var root = new ComponentTreeNode("category:" + group.Key, LocalizedStatus.Key("component_center.category." + group.Key));
            foreach (var component in group) root.Children.Add(Leaf(component.ComponentId));
            _componentRoots.Add(root);
        }
        foreach (var workload in ComponentCatalog.Workloads)
        {
            var root = new ComponentTreeNode("workload:" + workload.WorkloadId, LocalizedStatus.Key(workload.DisplayNameKey), workloadId: workload.WorkloadId);
            foreach (var id in workload.RequiredComponents) root.Children.Add(Leaf(id, workload.WorkloadId));
            foreach (var id in workload.OptionalComponents) root.Children.Add(Leaf(id, workload.WorkloadId, optional: true));
            _workloadRoots.Add(root);
        }
        foreach (var node in _componentRoots.Concat(_workloadRoots).SelectMany(root => root.DescendantsAndSelf()))
            node.SelectionRequested = SelectTreeNode;
    }
    private void RefreshTree()
    {
        var selected = Drafts.Select(item => item.Draft.ComponentId).ToHashSet(StringComparer.Ordinal);
        foreach (var root in _componentRoots.Concat(_workloadRoots))
        {
            root.SynchronizeSelection(selected);
            foreach (var node in root.DescendantsAndSelf())
            {
                node.CanSelect = !Runtime.IsBusy && !Runtime.IsInvalidated;
                if (node.ComponentId is null) continue;
                node.Status = Runtime.OwnershipText;
                node.Version = Runtime.MeasuredVersionText.Resolve();
            }
        }
        bool Eligible(ComponentTreeNode node) => SelectedPage switch
        {
            ComponentCenterPage.Installed => Runtime.Installation?.Installation.Ownership is RuntimeOwnership.Managed or RuntimeOwnership.External,
            ComponentCenterPage.Updates => Runtime.HasUpdates,
            _ => true
        };
        var roots = SelectedPage == ComponentCenterPage.Workloads ? _workloadRoots : _componentRoots;
        var visible = roots.Where(root => root.Filter(Search.Trim(), Eligible)).ToArray();
        if (!VisibleRoots.SequenceEqual(visible)) { VisibleRoots.Clear(); foreach (var root in visible) VisibleRoots.Add(root); }
        if (SelectedNode is null || !visible.SelectMany(root => root.VisibleDescendantsAndSelf()).Contains(SelectedNode))
            SelectedNode = SelectedPage == ComponentCenterPage.Workloads ? visible.FirstOrDefault()
                : visible.SelectMany(root => root.VisibleDescendantsAndSelf()).FirstOrDefault(node => node.ComponentId is not null);
        NotifyContext();
    }
    private void SelectTreeNode(ComponentTreeNode node, bool selected)
    {
        if (Runtime.IsBusy || Runtime.IsInvalidated) { RefreshTree(); return; }
        var ids = node.DescendantsAndSelf().Where(item => item.ComponentId is not null && (!selected || node.ComponentId is not null || !item.IsOptional))
            .Select(item => item.ComponentId!).ToHashSet(StringComparer.Ordinal);
        if (!selected)
        {
            foreach (var item in Drafts.Where(item => ids.Contains(item.Draft.ComponentId)).ToArray()) Drafts.Remove(item);
            Persist(); return;
        }
        if (!Runtime.CanManage) { Notice = LocalizedStatus.Key("proxy_server.runtime.requires_manage"); RefreshTree(); return; }
        if (Runtime.NeedsRecovery) { Notice = Runtime.RecoveryText; RefreshTree(); return; }
        if (Runtime.Installation?.PendingOperationId is not null)
        { Notice = LocalizedStatus.Key("component_center.availability.Busy"); RefreshTree(); return; }
        // Current adapter is sing-box. Resolve all referenced requirements before making any local change.
        var pending = new Queue<string>(ids);
        while (pending.TryDequeue(out var id))
        {
            if (ComponentCatalog.Find(id) is not { } definition) { Notice = LocalizedStatus.Key("component_center.component_unknown"); RefreshTree(); return; }
            foreach (var dependency in definition.RequiredComponents) if (ids.Add(dependency)) pending.Enqueue(dependency);
        }
        if (ids.Any(id => ComponentCatalog.Find(id)!.Conflicts.Any(ids.Contains))
            || Drafts.Any(item => ids.Contains(item.Draft.ComponentId) && item.Draft.Change == RuntimeChange.Uninstall))
        { Notice = LocalizedStatus.Key("component_center.draft_conflict"); RefreshTree(); return; }
        var missing = ids.Where(id => !Drafts.Any(item => item.Draft.ComponentId == id)).ToArray();
        if (missing.Length == 0) { RefreshTree(); return; }
        if (missing.Length != 1 || missing[0] != "sing-box" || Runtime.SelectedInstallDraft is not { } draft)
        {
            Notice = LocalizedStatus.Key(Runtime.Installation?.Installation.Ownership == RuntimeOwnership.Managed
                ? "component_center.explicit_action" : "component_center.select_version");
            RefreshTree(); return;
        }
        Stage(draft);
    }
    [RelayCommand] private void SelectWorkload() { if (SelectedNode is { } node) SelectTreeNode(node, true); }
    [RelayCommand] private Task RefreshAsync() => Runtime.EnsureLoadedAsync();

    public bool Stage(ComponentChangeDraft draft)
    {
        if (ComponentCatalog.Find(draft.ComponentId) is not { } definition || definition.RuntimeId != draft.RuntimeId)
            throw new ArgumentException("component_center.component_unknown");
        var existing = Drafts.SingleOrDefault(d => d.Draft.ComponentId == draft.ComponentId);
        if (existing is not null)
        {
            if (existing.Draft != draft) Notice = LocalizedStatus.Key("component_center.draft_conflict");
            return existing.Draft == draft;
        }
        Notice = LocalizedStatus.Literal("");
        Drafts.Add(new(draft));
        Persist();
        return true;
    }
    [RelayCommand] private void Remove(ComponentDraftItem? item)
    {
        if (item is null || Runtime.IsBusy) return;
        Drafts.Remove(item); Persist();
    }
    [RelayCommand] private void Discard()
    {
        if (Runtime.IsBusy) return;
        Notice = LocalizedStatus.Literal("");
        Drafts.Clear(); Persist();
    }
    [RelayCommand] private async Task PreviewAsync()
    {
        if (!CanPreview) return;
        // The first adapter is sing-box. Never silently expand this set to newly discovered updates.
        foreach (var item in Drafts.ToArray())
        {
            if (!await Runtime.ApplyDraftAsync(item.Draft)) break;
            Drafts.Remove(item); Persist();
        }
    }
    public void Persist()
    {
        _store.Save(_server, _actor, Drafts.Select(d => d.Draft));
        OnPropertyChanged(nameof(HasDrafts)); OnPropertyChanged(nameof(DraftSummary)); OnPropertyChanged(nameof(CanPreview));
        RefreshTree();
    }
    public void Dispose()
    {
        Runtime.StageChange = null;
        Runtime.PropertyChanged -= RuntimeChanged;
        Runtime.StateChanged -= RuntimeStateChanged;
    }
}
