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
        LocalizedStatus.Key(Draft.Change == RuntimeChange.Uninstall ? "proxy_server.runtime.uninstall_title"
            : "proxy_server.runtime.action." + Draft.Change), Draft.Version ?? "—");
}

/// <summary>Local drafts, frozen previews, and separately approved, durably tracked task execution.</summary>
public partial class ComponentCenterViewModel : ObservableObject, IDisposable
{
    private readonly ComponentDraftStore _store;
    private readonly string _server;
    private readonly Guid _actor;
    private readonly List<ComponentTreeNode> _componentRoots = [];
    private readonly List<ComponentTreeNode> _workloadRoots = [];
    /// <summary>Component id to its domain runtime page. Components absent here are not managed yet.</summary>
    private readonly IReadOnlyDictionary<string, ProxyServerRuntimeViewModel> _runtimes;
    /// <summary>Read-only observers for components whose lifecycle belongs to another application.</summary>
    private readonly IReadOnlyList<IComponentHostObserver> _observers;
    private readonly Dictionary<string, ComponentHostObservation> _hostObservations = new(StringComparer.Ordinal);
    public ObservableCollection<ComponentCenterNavigationItem> Pages { get; } = new(Enum.GetValues<ComponentCenterPage>().Select(page => new ComponentCenterNavigationItem(page)));
    public IEnumerable<ComponentCenterNavigationItem> BrowsePages => Pages.Where(item => item.Page is not ComponentCenterPage.Tasks and not ComponentCenterPage.Workloads);
    public IEnumerable<ComponentTreeNode> BatchComponents => _componentRoots.SelectMany(root => root.DescendantsAndSelf())
        .Where(node => node.ComponentId is { } id && ComponentCatalog.Find(id)?.Integration == ComponentIntegration.Batch);
    public IEnumerable<ComponentTreeNode> TaskWorkloads => _workloadRoots;
    [ObservableProperty] private ComponentTreeNode? taskWorkload;
    public bool CanCloseTaskDialog => !IsTaskBusy && !IsInspecting;
    public Func<Task>? ShowTasksDialogAsync { get; set; }
    [RelayCommand] private Task OpenTasksAsync() => ShowTasksDialogAsync?.Invoke() ?? Task.CompletedTask;
    [RelayCommand] private void AddTaskWorkload() { if (TaskWorkload is { } node) SelectTreeNode(node, true); }
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
    public ComponentTreeNode? SelectedComponent => SelectedNode?.ComponentId is null ? null : SelectedNode;
    public HostComponentDefinition? SelectedDefinition => SelectedNode?.ComponentId is { } id ? ComponentCatalog.Find(id) : null;
    /// <summary>The detail pane follows the selected component instead of assuming sing-box.</summary>
    public ProxyServerRuntimeViewModel? DetailRuntime => SelectedDefinition?.ComponentId is { } id && _runtimes.TryGetValue(id, out var runtime)
        ? runtime : null;
    public bool HasRuntimeDetails => HasResults && DetailRuntime is not null;
    public bool ShowRuntimeTaskActivity => HasRuntimeDetails && DetailRuntime is { } runtime
        && (runtime.HasActiveOperation || runtime.NeedsRecovery || !runtime.OperationText.IsEmpty || !runtime.OutcomeText.IsEmpty);
    public bool HasWorkloadDetails => HasResults && SelectedNode?.WorkloadId is not null && SelectedNode.ComponentId is null;
    /// <summary>True when the selected component has no runtime page to drive, whatever the reason.</summary>
    public bool ShowNotIntegrated => HasResults && SelectedDefinition is { } definition
        && (definition.Integration == ComponentIntegration.Application || DetailRuntime is null)
        && !(definition.Integration == ComponentIntegration.Installation && PackageInstallation is not null);
    public int SelectedSourceKindCount => SelectedDefinition?.Sources.Count ?? 0;
    public LocalizedStatus SelectedComponentDescription => SelectedDefinition is { } definition
        ? LocalizedStatus.Key(definition.DescriptionKey) : LocalizedStatus.Literal("");
    /// <summary>The platform the Host reported for itself. Empty until an observation succeeded.</summary>
    public string HostPlatform => SelectedComponentState?.State?.Target.Os ?? Runtime.Installation?.Installation.Target.Os ?? "";
    /// <summary>Declared provenance for this host's platforms, plus everything declared elsewhere.</summary>
    public IReadOnlyList<ComponentSourceChip> SelectedHostSources => SourceChips(hostPlatformOnly: true);
    public IReadOnlyList<ComponentSourceChip> SelectedOtherSources => SourceChips(hostPlatformOnly: false);
    public bool HasSelectedSources => SelectedHostSources.Count + SelectedOtherSources.Count > 0;
    public bool HasOtherPlatformSources => SelectedOtherSources.Count > 0;
    private IReadOnlyList<ComponentSourceChip> SourceChips(bool hostPlatformOnly)
    {
        if (SelectedDefinition is not { } definition) return [];
        var host = HostPlatform;
        return definition.Sources
            .Where(source => (host.Length != 0 && source.Platforms.Contains(host)) == hostPlatformOnly)
            .Select(source => new ComponentSourceChip(
                LocalizedStatus.Format("component_center.source_kind_entry",
                    LocalizedStatus.Key("component_center.source_kind." + source.Kind),
                    LocalizedStatus.Key(source.AdapterAvailable ? "component_center.source_ready" : "component_center.source_planned")),
                PlatformName(source.Platforms), source.AdapterAvailable, hostPlatformOnly))
            .ToArray();
    }
    private static LocalizedStatus PlatformName(IReadOnlyList<string> platforms) => platforms.Count switch
    {
        1 => platforms[0] switch
        {
            "linux" => LocalizedStatus.Key("component_center.platform.linux", "Linux"),
            "windows" => LocalizedStatus.Key("component_center.platform.windows", "Windows"),
            _ => LocalizedStatus.Literal(platforms[0])
        },
        _ => LocalizedStatus.Key("component_center.platform.multiple")
    };
    public LocalizedStatus SelectedIntegrationText => SelectedDefinition?.ComponentId == "git"
        ? LocalizedStatus.Key("component_center.git.integration") : SelectedDefinition is { } definition
        ? LocalizedStatus.Key("component_center.integration." + definition.Integration) : LocalizedStatus.Literal("");
    public LocalizedStatus SelectedCategoryText => SelectedDefinition is { } definition
        ? LocalizedStatus.Key("component_center.category." + definition.Category) : LocalizedStatus.Literal("");
    public LocalizedStatus SelectedApplicationText => SelectedDefinition?.ApplicationId is { } app
        ? LocalizedStatus.Key("application." + app + ".display_name") : LocalizedStatus.Key("component_center.no_application");
    public bool CanOpenSelectedApplication => SelectedDefinition?.ApplicationId is not null;
    /// <summary>The warning card already offers the same action for a component it does not manage.</summary>
    public bool ShowHeaderOpenApplication => CanOpenSelectedApplication && !ShowNotIntegrated;
    public string? SelectedApplicationId => SelectedDefinition?.ApplicationId;
    /// <summary>True while the selected component still uses the batch draft and TaskPlan path.</summary>
    public bool SelectedUsesBatch => SelectedDefinition?.Integration == ComponentIntegration.Batch;
    public ComponentStatusTone SelectedTone => Tone(SelectedDefinition);
    public bool SelectedToneOk => SelectedTone == ComponentStatusTone.Ok;
    public bool SelectedToneWarning => SelectedTone == ComponentStatusTone.Warning;
    public bool SelectedToneDanger => SelectedTone == ComponentStatusTone.Danger;
    public bool SelectedToneIdle => SelectedTone == ComponentStatusTone.Idle;
    public LocalizedStatus SelectedStatusText
    {
        get
        {
            if (SelectedDefinition is not { } definition) return LocalizedStatus.Literal("");
            if (definition.ComponentId == "git") return GitStatusText;
            if (SelectedComponentState?.State is { } packageState)
                return packageState.RecoveryRequired ? LocalizedStatus.Key("component_center.badge.Recovery")
                    : LocalizedStatus.Key("proxy_server.runtime.ownership." + packageState.Ownership);
            var runtime = DetailRuntime;
            if (runtime is null && definition.Integration == ComponentIntegration.Installation && PackageInstallation is not null)
                return LocalizedStatus.Key("proxy_server.state.unknown");
            // A component with no runtime page in this build has no measured state to claim.
            if (definition.Integration == ComponentIntegration.Application || runtime is null)
                return LocalizedStatus.Key("component_center.status.not_integrated");
            if (runtime.Installation is null) return LocalizedStatus.Key("proxy_server.state.unknown");
            if (runtime.NeedsRecovery) return LocalizedStatus.Key("component_center.badge.Recovery");
            if (runtime.HasRuntimeProblem) return LocalizedStatus.Key("component_center.badge.Faulted");
            return runtime.OwnershipText;
        }
    }
    public LocalizedStatus SelectedVersionText => SelectedDefinition?.ComponentId == "git"
        ? LocalizedStatus.Literal(GitEngine?.IsGitAvailable == true ? GitEngine.EngineVersion : "—") : SelectedComponentState?.State?.ActiveVersion is { } version
        ? LocalizedStatus.Literal(version) : DetailRuntime?.MeasuredVersionText ?? LocalizedStatus.Literal("—");
    public bool HasSelectedVersion => SelectedDefinition?.ComponentId == "git" && GitEngine?.IsGitAvailable == true || SelectedComponentState?.State?.ActiveVersion is { Length: > 0 }
        || DetailRuntime?.Installation?.MeasuredVersion is { Length: > 0 };
    public bool SelectedHasUpdate => DetailRuntime?.HasUpdates == true;
    /// <summary>The host target chip is only meaningful once the host reported a target.</summary>
    public bool HasTargetText => Runtime.Installation?.Installation.Target is not null;
    public bool HasComponentDetails => HasResults && SelectedDefinition is not null;
    /// <summary>A runtime message belongs to the component that produced it, never to another row.</summary>
    public LocalizedStatus HeaderOutcomeText => DetailRuntime?.OutcomeText ?? LocalizedStatus.Literal("");
    public bool HasHeaderOutcome => !HeaderOutcomeText.IsEmpty;
    public LocalizedStatus SelectedApplicationOpenText => LocalizedStatus.Format("component_center.open_application_named", SelectedApplicationText);
    public LocalizedStatus NotIntegratedMessage => LocalizedStatus.Format("component_center.not_integrated_message", SelectedApplicationText);
    /// <summary>The footer is the only carrier of unapplied intent; it never hides an existing draft.</summary>
    public bool ShowDraftPanel => HasDrafts;
    /// <summary>The freeze-plan card only applies to the selected batch component, never to another row.</summary>
    public bool ShowBatchPlanPanel => HasDrafts;
    [ObservableProperty] private bool isObservingHost;
    [ObservableProperty] private LocalizedStatus hostObservationNotice = LocalizedStatus.Literal("");
    public ComponentHostObservation? HostObservation => SelectedDefinition?.ComponentId is { } id
        ? _hostObservations.GetValueOrDefault(id) : null;
    public bool HasHostObservation => HostObservation is not null;
    public bool ShowHostObservation => CanObserveHost && SelectedDefinition is { Integration: ComponentIntegration.Application or ComponentIntegration.Installation };
    public bool CanObserveHost => !IsObservingHost && SelectedDefinition is { } definition
        && _observers.Any(observer => observer.CanObserve(definition.ComponentId));
    public LocalizedStatus HostObservationPlatformText => LocalizedStatus.Format("component_center.observation.platform",
        HostObservation?.Platform ?? LocalizedStatus.Literal("—"));
    public LocalizedStatus HostObservationManagementText => LocalizedStatus.Format("component_center.observation.management",
        HostObservation?.Management ?? LocalizedStatus.Literal("—"));
    public LocalizedStatus HostObservationVersionText => LocalizedStatus.Format("component_center.observation.version",
        HostObservation?.Version ?? LocalizedStatus.Literal("—"));
    public LocalizedStatus HostObservationStateText => LocalizedStatus.Format("component_center.observation.state",
        HostObservation?.State ?? LocalizedStatus.Literal("—"));
    public LocalizedStatus HostObservationNoteText => HostObservation?.Note ?? LocalizedStatus.Literal("");
    /// <summary>Reads the owning application's own facts. It never installs, plans or approves.</summary>
    [RelayCommand] private async Task ObserveHostAsync()
    {
        if (SelectedDefinition is not { } definition) return;
        var observer = _observers.FirstOrDefault(item => item.CanObserve(definition.ComponentId));
        if (observer is null || IsObservingHost) return;
        IsObservingHost = true;
        HostObservationNotice = LocalizedStatus.Key("component_center.observation.running");
        NotifyContext();
        try
        {
            var observation = await observer.ObserveAsync(definition.ComponentId, HostPlatform, _inspectionLifetime.Token);
            if (observation is not null) _hostObservations[definition.ComponentId] = observation;
            HostObservationNotice = LocalizedStatus.Key("component_center.observation.complete");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException)
        { HostObservationNotice = LocalizedStatus.Key("component_center.observation.failed"); }
        finally { IsObservingHost = false; NotifyContext(); }
    }
    public LocalizedStatus SelectedTitle => SelectedPage == ComponentCenterPage.Tasks ? LocalizedStatus.Key("component_center.page.Tasks")
        : SelectedNode?.Label ?? LocalizedStatus.Key("component_center.choose_component");
    /// <summary>The identity card always names the component, never the page it is shown on.</summary>
    public LocalizedStatus SelectedComponentTitle => SelectedNode?.Label ?? LocalizedStatus.Key("component_center.choose_component");
    public LocalizedStatus EmptyMessage => LocalizedStatus.Key(!string.IsNullOrWhiteSpace(Search)
        ? "component_center.no_matches"
        : SelectedPage is ComponentCenterPage.Installed or ComponentCenterPage.Updates
            && Runtime.Installation?.Installation.Ownership is null or RuntimeOwnership.Unknown
        ? "component_center.observation_unknown"
        : SelectedPage != ComponentCenterPage.Updates ? "component_center.no_matches"
        : Runtime.Installation?.Installation.Ownership != RuntimeOwnership.Managed ? "component_center.updates_requires_managed"
        : !Runtime.CanDetermineUpdates ? "component_center.updates_unknown" : "component_center.no_updates");
    public LocalizedStatus WorkloadRequirements => LocalizedStatus.Literal(SelectedNode?.WorkloadId is { } id
        ? string.Join(", ", ComponentCatalog.Workloads.Single(workload => workload.WorkloadId == id).RequiredComponents) : "");
    public LocalizedStatus WorkloadOptional => LocalizedStatus.Literal(SelectedNode?.WorkloadId is { } id
        ? string.Join(", ", ComponentCatalog.Workloads.Single(workload => workload.WorkloadId == id).OptionalComponents) : "");
    public ProxyServerRuntimeViewModel Runtime { get; }
    public string HostIdentity => _server;
    public ObservableCollection<ComponentDraftItem> Drafts { get; } = [];
    public bool HasDrafts => Drafts.Count > 0;
    public LocalizedStatus PersistenceNotice => LocalizedStatus.Key(_store.HasPersistenceFailure(_server, _actor)
        ? "component_center.persistence_failed" : "component_center.persistence_hint");
    public bool CanPreview => HasDrafts && !IsTaskBusy && !IsInspecting && !Runtime.IsBusy && Runtime.CanManage && !Runtime.IsInvalidated && Runtime.Installation is not null;
    public LocalizedStatus DraftSummary => LocalizedStatus.Format("component_center.pending", Drafts.Count);
    [ObservableProperty] private LocalizedStatus notice = LocalizedStatus.Literal("");

    public ComponentCenterViewModel(ProxyServerRuntimeViewModel runtime, ComponentDraftStore store, string server, Guid actor,
        ITaskPlanPreviewClient? previews = null, ITaskExecutionClient? execution = null, ComponentTaskReferenceStore? references = null,
        IReadOnlyDictionary<string, ProxyServerRuntimeViewModel>? additionalRuntimes = null,
        IReadOnlyList<IComponentHostObserver>? hostObservers = null, IComponentLifecycleClient? lifecycle = null)
    {
        Runtime = runtime; _store = store; _server = server; _actor = actor;
        _previews = previews;
        _execution = execution; _references = references;
        _observers = hostObservers ?? [];
        _lifecycle = lifecycle;
        var runtimes = new Dictionary<string, ProxyServerRuntimeViewModel>(StringComparer.Ordinal) { ["sing-box"] = runtime, [runtime.RuntimeId] = runtime };
        foreach (var pair in additionalRuntimes ?? new Dictionary<string, ProxyServerRuntimeViewModel>()) runtimes[pair.Key] = pair.Value;
        _runtimes = runtimes;
        _inspectionInstallation = runtime.Installation;
        _inspectionCatalog = runtime.CatalogSnapshot;
        BuildTrees();
        TaskWorkload = _workloadRoots.FirstOrDefault();
        foreach (var draft in store.Read(server, actor)) Drafts.Add(new(draft));
        SelectedNavigation = Pages[0];
        RefreshTree();
        runtime.StageChange = Stage;
        runtime.PropertyChanged += RuntimeChanged;
        runtime.StateChanged += RuntimeStateChanged;
        foreach (var secondary in _runtimes.Values.Distinct().Where(item => !ReferenceEquals(item, runtime)))
        {
            secondary.PropertyChanged += SecondaryRuntimeChanged;
            secondary.StateChanged += RuntimeStateChanged;
        }
        ReloadTaskHistory();
    }
    /// <summary>Secondary runtimes only refresh presentation; task and draft authority stays with sing-box.</summary>
    private void SecondaryRuntimeChanged(object? sender, PropertyChangedEventArgs e) => NotifyContext();
    private void RuntimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProxyServerRuntimeViewModel.CanRead) or nameof(ProxyServerRuntimeViewModel.CanManage)) _taskAccessRevision++;
        if (!Runtime.CanRead || !Runtime.CanManage || Runtime.IsInvalidated) _driveTask = false;
        if (!Runtime.CanRead || Runtime.IsInvalidated) { TaskRecovery = null; ClearTaskEvidence(); }
        OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(CanInspect)); OnPropertyChanged(nameof(CanPlanDraft)); TaskProperties();
        NotifyContext();
    }
    private void RuntimeStateChanged(object? sender, EventArgs e) { ObserveInspectionState(); RefreshTree(); }
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
    public RelaxKonOS.Client.Apps.WebServers.WebServerManagerViewModel? NginxSelection { get; set; }
    public bool ShowNginxSelection => SelectedDefinition?.ComponentId == "nginx" && NginxSelection is not null;

    public RelaxKonOS.Client.Apps.Git.GitClientViewModel? GitEngine { get; set; }
    public bool ShowGitInstallation => SelectedDefinition?.ComponentId == "git" && GitEngine is not null;
    public LocalizedStatus GitStatusText => LocalizedStatus.Key(GitEngine?.IsGitAvailable == true
        ? "component_center.domain.available" : GitEngine?.IsGitInstallRequired == true
            ? "component_center.domain.absent" : "proxy_server.state.unknown");
    public void RefreshGitState() => RefreshTree();
    public bool GitCanManage { get; set; }
    public bool CanInstallGit => GitCanManage && ShowGitInstallation && GitEngine!.CanAutoInstall && !GitEngine.IsGitAvailable
        && Runtime.CanManage && !Runtime.IsInvalidated && PackageInstallation is { IsActive: false };
    public Func<Task<bool>>? ConfirmGitInstallAsync { get; set; }
    public void NotifyGit()
    {
        OnPropertyChanged(nameof(CanInstallGit));
        OnPropertyChanged(nameof(SelectedStatusText));
        OnPropertyChanged(nameof(SelectedVersionText));
        OnPropertyChanged(nameof(HasSelectedVersion));
        InstallGitCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanInstallGit))] private async Task InstallGitAsync()
    {
        if (!CanInstallGit || ConfirmGitInstallAsync is null || !await ConfirmGitInstallAsync() || !CanInstallGit) return;
        await PackageInstallation!.SubmitAsync(RelaxKonOS.Protocol.Installations.InstallationOperationKind.Install,
            new RelaxKonOS.Protocol.Installations.GitInstallationRequest(true));
        NotifyGit();
    }

    private void NotifyContext()
    {
        foreach (var property in new[] { nameof(HasResults), nameof(HasRuntimeDetails), nameof(HasWorkloadDetails),
            nameof(SelectedTitle), nameof(EmptyMessage), nameof(WorkloadRequirements), nameof(WorkloadOptional), nameof(ShowInspection),
            nameof(SelectedComponentTitle), nameof(ShowNginxSelection), nameof(ShowGitInstallation), nameof(CanInstallGit),
            nameof(DetailRuntime), nameof(ShowRuntimeTaskActivity), nameof(SelectedComponent), nameof(SelectedDefinition), nameof(ShowNotIntegrated),
            nameof(SelectedComponentDescription), nameof(SelectedHostSources), nameof(SelectedOtherSources),
            nameof(HasSelectedSources), nameof(HasOtherPlatformSources), nameof(HostPlatform),
            nameof(HasHostObservation), nameof(HostObservation), nameof(ShowHostObservation), nameof(CanObserveHost),
            nameof(HostObservationPlatformText), nameof(HostObservationManagementText), nameof(HostObservationVersionText),
            nameof(HostObservationStateText), nameof(HostObservationNoteText),
            nameof(SelectedIntegrationText), nameof(SelectedCategoryText), nameof(SelectedApplicationText),
            nameof(CanOpenSelectedApplication), nameof(SelectedApplicationId), nameof(SelectedUsesBatch), nameof(ShowHeaderOpenApplication),
            nameof(SelectedTone), nameof(SelectedToneOk), nameof(SelectedToneWarning), nameof(SelectedToneDanger), nameof(SelectedToneIdle),
            nameof(SelectedStatusText), nameof(SelectedVersionText), nameof(SelectedHasUpdate), nameof(CanPreview), nameof(CanPlanDraft),
            nameof(HasComponentDetails), nameof(HeaderOutcomeText), nameof(HasHeaderOutcome),
            nameof(SelectedApplicationOpenText), nameof(NotIntegratedMessage), nameof(ShowDraftPanel),
            nameof(ShowBatchPlanPanel), nameof(HasSelectedVersion), nameof(HasTargetText) })
            OnPropertyChanged(property);
        NotifyPackage();
        NotifyGit();
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
                if (node.ComponentId is not { } id || ComponentCatalog.Find(id) is not { } definition) continue;
                var runtime = _runtimes.GetValueOrDefault(id);
                // Only the batch-integrated component stages drafts here; other rows stay read-only.
                node.CanSelect = !IsTaskBusy && !(runtime?.IsBusy ?? false) && definition.Integration == ComponentIntegration.Batch;
                node.ShowStatus = true;
                var packageState = _componentStates.GetValueOrDefault(id)?.State;
                node.ShowVersion = id == "git" ? GitEngine?.IsGitAvailable == true : runtime?.Installation is not null || packageState?.ActiveVersion is not null;
                node.Status = id == "git" ? GitStatusText : packageState is not null ? LocalizedStatus.Key("proxy_server.runtime.ownership." + packageState.Ownership)
                    : runtime is null ? LocalizedStatus.Key(_packageInstallations.ContainsKey(id) ? "proxy_server.state.unknown" : "component_center.status.not_integrated")
                    : runtime.NeedsRecovery ? LocalizedStatus.Key("component_center.badge.Recovery")
                    : runtime.HasRuntimeProblem ? LocalizedStatus.Key("component_center.badge.Faulted")
                    : runtime.OwnershipText;
                node.Version = id == "git" ? GitEngine?.IsGitAvailable == true ? GitEngine.EngineVersion : "" : packageState?.ActiveVersion ?? runtime?.MeasuredVersionText.Resolve() ?? "";
                node.SetTone(Tone(definition));
                node.HasUpdate = runtime?.HasUpdates == true;
            }
        }
        bool Eligible(ComponentTreeNode node) => SelectedPage switch
        {
            ComponentCenterPage.Installed => node.ComponentId is { } installed
                && (installed == "git" ? GitEngine?.IsGitAvailable == true : (RuntimeFor(installed)?.Installation?.Installation.Ownership ?? _componentStates.GetValueOrDefault(installed)?.State?.Ownership)
                    is RuntimeOwnership.Managed or RuntimeOwnership.External),
            ComponentCenterPage.Updates => node.ComponentId is { } updating && RuntimeFor(updating)?.HasUpdates == true,
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
    private ProxyServerRuntimeViewModel? RuntimeFor(string componentId) => _runtimes.GetValueOrDefault(componentId);
    /// <summary>Presentation tone from measured facts only; unknown and external stay neutral.</summary>
    private ComponentStatusTone Tone(HostComponentDefinition? definition)
    {
        if (definition is null || definition.Integration == ComponentIntegration.Application) return ComponentStatusTone.Idle;
        var runtime = RuntimeFor(definition.ComponentId);
        if (runtime?.Installation is not { } snapshot) return ComponentStatusTone.Idle;
        if (runtime.NeedsRecovery) return ComponentStatusTone.Warning;
        if (runtime.HasRuntimeProblem) return ComponentStatusTone.Danger;
        return snapshot.Installation.Ownership switch
        {
            RuntimeOwnership.Managed => ComponentStatusTone.Ok,
            RuntimeOwnership.Absent => ComponentStatusTone.Warning,
            _ => ComponentStatusTone.Idle
        };
    }
    private void SelectTreeNode(ComponentTreeNode node, bool selected)
    {
        if (Runtime.IsBusy || Runtime.IsInvalidated) { RefreshTree(); return; }
        if (node.ComponentId is { } staged && ComponentCatalog.Find(staged) is { Integration: not ComponentIntegration.Batch } stagedDefinition)
        {
            Notice = LocalizedStatus.Key(stagedDefinition.Integration == ComponentIntegration.Runtime
                ? "component_center.integration_runtime_only" : "component_center.integration_application_only");
            RefreshTree(); return;
        }
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
    /// <summary>Refreshes every component that has a runtime, not just the batch-integrated one.</summary>
    [RelayCommand] private async Task RefreshAsync()
    {
        foreach (var runtime in _runtimes.Values.Distinct())
            if (runtime.CanRead && !runtime.IsInvalidated) await runtime.EnsureLoadedAsync();
    }

    public bool Stage(ComponentChangeDraft draft)
    {
        if (IsTaskBusy) return false;
        if (ComponentCatalog.Find(draft.ComponentId) is not { } definition || definition.RuntimeId != draft.RuntimeId)
            throw new ArgumentException("component_center.component_unknown");
        var existing = Drafts.SingleOrDefault(d => d.Draft.ComponentId == draft.ComponentId);
        if (existing is not null)
        {
            if (existing.Draft != draft) Notice = LocalizedStatus.Key("component_center.draft_conflict");
            _ = OpenTasksAsync();
            return existing.Draft == draft;
        }
        Notice = LocalizedStatus.Literal("");
        Drafts.Add(new(draft));
        Persist();
        _ = OpenTasksAsync();
        return true;
    }
    [RelayCommand] private void Remove(ComponentDraftItem? item)
    {
        if (item is null || IsTaskBusy || Runtime.IsBusy) return;
        Drafts.Remove(item); Persist();
    }
    [RelayCommand] private void Discard()
    {
        if (IsTaskBusy || Runtime.IsBusy) return;
        Notice = LocalizedStatus.Literal("");
        Drafts.Clear(); Persist();
    }
    public void Persist()
    {
        _draftRevision++;
        ClearChangePlan();
        _store.Save(_server, _actor, Drafts.Select(d => d.Draft));
        OnPropertyChanged(nameof(PersistenceNotice));
        OnPropertyChanged(nameof(HasDrafts)); OnPropertyChanged(nameof(DraftSummary)); OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(ShowDraftPanel)); OnPropertyChanged(nameof(ShowBatchPlanPanel));
        RefreshTree();
    }
    public void Dispose()
    {
        foreach (var installation in _packageInstallations.Values)
        {
            installation.PropertyChanged -= PackageInstallationChanged;
            installation.Dispose();
        }
        _taskDisposed = true; _driveTask = false;
        _inspectionLifetime.Cancel(); _inspectionLifetime.Dispose();
        Runtime.StageChange = null;
        Runtime.PropertyChanged -= RuntimeChanged;
        Runtime.StateChanged -= RuntimeStateChanged;
        foreach (var secondary in _runtimes.Values.Distinct().Where(item => !ReferenceEquals(item, Runtime)))
        {
            secondary.PropertyChanged -= SecondaryRuntimeChanged;
            secondary.StateChanged -= RuntimeStateChanged;
        }
    }
}
