using System.Collections.ObjectModel;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Client.Apps.ComponentCenter;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Client.Apps.ProxyServer;

/// <summary>
/// One catalog offer with the independent admission dimensions and the exact block reason. Availability
/// here is display only: every mutation still goes through the Server plan and the Helper's own trust check.
/// </summary>
public sealed record RuntimeReleaseOption(RuntimeRelease Release, bool Installed, LocalizedStatus Blocked)
{
    public string Version => Release.OriginalVersion;
    public LocalizedStatus ChannelLabel => LocalizedStatus.Key("proxy_server.runtime.channel." + Release.Channel, Release.Channel.ToString());
    public LocalizedStatus TrustLabel => LocalizedStatus.Key("proxy_server.runtime.trust." + Release.Trust, Release.Trust.ToString());
    public LocalizedStatus SupportLabel => LocalizedStatus.Key(Release.ConfigurationSupported
        ? "proxy_server.runtime.support.supported" : "proxy_server.runtime.support.unsupported");
    public LocalizedStatus EvidenceLabel => LocalizedStatus.Key("proxy_server.runtime.evidence." + Release.Evidence, Release.Evidence.ToString());
    public string Published => Release.PublishedAt?.UtcDateTime.ToString("yyyy-MM-dd") ?? "—";
    public bool Available => Blocked.IsEmpty;
    public bool HasBlocked => !Blocked.IsEmpty;
    public bool IsInstalled => Installed;
}

/// <summary>
/// Managed runtime page: version discovery, target selection, and guarded execution. It never renders a
/// version list as installable when the Host has not both confirmed the source and declared configuration support.
/// </summary>
public partial class ProxyServerRuntimeViewModel(IRuntimeCatalogClient client) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private RuntimeInstallationSnapshot? _installation;
    private RuntimeRecoverySnapshot? _recovery;
    private RuntimeCatalogSnapshot? _catalog;
    private bool _invalidated;
    private bool _loading;
    private bool _refreshFailed;
    /// <summary>The submitted operation and whether the Host still allows abandoning it.</summary>
    private Guid? _cancelTarget;
    private Guid? _cancelRequested;
    private bool _operationCancellable;
    private InstallationOperationDto? _observedOperation;
    /// <summary>The measurement behind a refused plan, so the page can name the fact that failed.</summary>
    private RuntimeDependencyReport? _blockedDependencies;

    private static readonly string[] DerivedProperties =
    [
        nameof(CanRefreshSources), nameof(HasSources), nameof(HasReleases), nameof(HasSource), nameof(CanAct), nameof(CanRefreshCatalog),
        nameof(OwnershipText), nameof(TargetText), nameof(MeasuredVersionText), nameof(ActiveRevisionText),
        nameof(InstalledReleasesText), nameof(PendingText), nameof(RuntimeProblemText), nameof(PublishedConfigurationText),
        nameof(SourceSummary), nameof(CatalogText), nameof(CatalogProblem), nameof(ActionText), nameof(ShowPrereleaseToggle),
        nameof(ReleaseCountText), nameof(HasRuntimeProblem), nameof(CanUninstall), nameof(NeedsRecovery), nameof(RecoveryText),
        nameof(RecoveryOperationText), nameof(RecoveryChangeText), nameof(RecoveryStageText), nameof(RecoveryOriginalProblem),
        nameof(RecoveryProblem), nameof(RecoveryServiceText), nameof(HasRecoveryServiceProblem),
        nameof(CanRetryRecovery), nameof(CanEmergencyStop), nameof(CanCloseRecovery), nameof(CanCancelDownload),
        nameof(HasEnvironment), nameof(EnvironmentPlatformText), nameof(EnvironmentServiceHostText),
        nameof(EnvironmentDiskSpaceText), nameof(EnvironmentFileSystemText), nameof(EnvironmentKernelText),
        nameof(HasEnvironmentWarning), nameof(EnvironmentWarning), nameof(DependencyText), nameof(HasDependencyBlock),
        nameof(Installation), nameof(IsInvalidated), nameof(Updates), nameof(HasUpdates), nameof(SelectedInstallDraft), nameof(InstallModeText), nameof(HasActiveOperation),
        nameof(OperationProgress), nameof(IsOperationIndeterminate)
    ];

    public event EventHandler? StateChanged;
    private void NotifyDerived() { foreach (var name in DerivedProperties) OnPropertyChanged(name); StateChanged?.Invoke(this, EventArgs.Empty); }

    [ObservableProperty] private bool canRead;
    [ObservableProperty] private bool canManage;
    public Action? RefreshAccess { get; set; }
    public void SynchronizeAccess() => RefreshAccess?.Invoke();
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool includePrerelease;
    [ObservableProperty] private LocalizedStatus outcomeText = LocalizedStatus.Literal("");
    [ObservableProperty] private LocalizedStatus operationText = LocalizedStatus.Literal("");
    [ObservableProperty] private LocalizedStatus planText = LocalizedStatus.Literal("");
    [ObservableProperty] private RuntimeSourceView? selectedSource;
    [ObservableProperty] private RuntimeReleaseOption? selectedRelease;

    /// <summary>Without a confirm host a destructive removal does nothing, exactly like the publication commands.</summary>
    public Func<LocalizedStatus, LocalizedStatus, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Collects the host administrator password when the Server answers "elevation-required".</summary>
    public Func<Task<string?>>? PasswordPromptAsync { get; set; }

    /// <summary>Component Center stages local intent; other runtime hosts execute through the guarded plan flow.</summary>
    public Func<ComponentChangeDraft, bool>? StageChange { get; set; }
    public RuntimeInstallationSnapshot? Installation => _installation;
    public RuntimeCatalogSnapshot? CatalogSnapshot => _catalog;
    public bool IsInvalidated => _invalidated;

    private ComponentChangeDraft Draft(RuntimeChange change, RuntimeRelease? release, bool keepData) => new(
        client.RuntimeId, client.RuntimeId, _installation!.Installation.HostId,
        _installation.Installation.InstallationId, _catalog?.Source.Identity.SourceId,
        release?.ReleaseId, release?.OriginalVersion, change, keepData, IncludePrerelease);

    public ObservableCollection<RuntimeSourceView> Sources { get; } = [];
    public ObservableCollection<RuntimeReleaseOption> Releases { get; } = [];

    public IReadOnlyList<RuntimeReleaseOption> Updates => _installation?.Installation.Ownership == RuntimeOwnership.Managed
        ? Releases.Where(option => option.Available && option.Release.Channel == RuntimeChannel.Stable
            && IsNewer(option.Release)).ToArray() : [];
    public bool HasUpdates => Updates.Count > 0;
    public ComponentChangeDraft? SelectedInstallDraft => _installation is { Installation.Ownership: RuntimeOwnership.Absent, PendingOperationId: null }
        && CanRead && CanManage && !_invalidated && !IsBusy && !NeedsRecovery && SelectedRelease is { Available: true } option
            ? Draft(RuntimeChange.Install, option.Release, keepData: true) : null;
    private bool IsNewer(RuntimeRelease release) => RuntimeSemanticVersion.TryParse(_installation?.MeasuredVersion, out var current)
        && RuntimeSemanticVersion.TryParse(release.SortVersion, out var target) && target.CompareTo(current) > 0;

    public bool CanRefreshSources => CanRead && !IsBusy && !_invalidated;
    public bool HasSources => Sources.Count > 0;
    public bool HasSource => _catalog is not null;
    public bool HasReleases => Releases.Count > 0;
    public bool CanRefreshCatalog => CanRead && CanManage && !IsBusy && !_invalidated && SelectedSource is not null;
    public bool CanAct => CanRead && CanManage && !IsBusy && !_invalidated && !NeedsRecovery
        && SelectedRelease is { Available: true } option && ChangeFor(option.Release) is not null
        && _installation is { PendingOperationId: null };
    public bool CanUninstall => CanRead && CanManage && !IsBusy && !_invalidated && !NeedsRecovery
        && _installation is { Installation.Ownership: RuntimeOwnership.Managed, PendingOperationId: null };
    public bool ShowPrereleaseToggle => true;

    /// <summary>
    /// An interrupted operation or a recorded compensation failure is a recovery state: the Host keeps rejecting
    /// new lifecycle changes until its own recovery entry runs, so the page must say so instead of offering actions.
    /// </summary>
    public bool NeedsRecovery => _recovery?.Required == true
        || _installation is { Installation.RecoveryRequired: true } or { PendingOperationId: not null, PendingStage: null };
    public LocalizedStatus RecoveryText => NeedsRecovery
        ? LocalizedStatus.Key("proxy_server.runtime.recovery_required") : LocalizedStatus.Literal("");

    public LocalizedStatus RecoveryOperationText => LocalizedStatus.Literal(_recovery?.OperationId?.ToString("D") ?? "—");
    public LocalizedStatus RecoveryChangeText => _recovery?.Change is { } change
        ? LocalizedStatus.Key("proxy_server.runtime.change." + change, change.ToString()) : LocalizedStatus.Literal("—");
    public LocalizedStatus RecoveryStageText => LocalizedStatus.Literal(_recovery?.Stage?.ToString() ?? "—");
    public LocalizedStatus RecoveryOriginalProblem => _recovery?.ProblemCode is { } problem
        ? ProblemText(problem) : LocalizedStatus.Literal("—");
    public LocalizedStatus RecoveryProblem => _recovery?.RecoveryProblemCode is { } problem
        ? ProblemText(problem) : LocalizedStatus.Literal("—");
    /// <summary>Nothing running and nothing registered are different facts; an unobserved service is said to be unknown.</summary>
    public LocalizedStatus RecoveryServiceText => _recovery?.ServiceProblem is { } problem ? ProblemText(problem)
        : _recovery?.Service is not { } service ? LocalizedStatus.Key("proxy_server.runtime.recovery_service.Unknown")
        : LocalizedStatus.Key(service.Running ? "proxy_server.runtime.recovery_service.Running"
            : service.Exists ? "proxy_server.runtime.recovery_service.Stopped"
            : "proxy_server.runtime.recovery_service.Absent");
    public bool HasRecoveryServiceProblem => _recovery?.ServiceProblem is not null;
    public bool CanRetryRecovery => CanRead && CanManage && !IsBusy && !_invalidated && _recovery is { Required: true };
    public bool CanEmergencyStop => CanRetryRecovery && client.SupportsRecoveryStopAndClose && _recovery?.Service is { Running: true };
    /// <summary>Closing is only offered once the Host proves nothing runs and its own compensation already failed.</summary>
    public bool CanCloseRecovery => CanRetryRecovery && client.SupportsRecoveryStopAndClose && _recovery is { NeedsOperatorDecision: true, Service.Running: false };

    /// <summary>
    /// Offered only while the Host itself reports the running operation as cancellable, so the page never shows
    /// a button the transaction would silently ignore after its switch checkpoint.
    /// </summary>
    public bool CanCancelDownload => CanRead && CanManage && !_invalidated && _cancelTarget is not null
        && _cancelRequested != _cancelTarget && _operationCancellable;
    public bool HasActiveOperation => (_installation?.PendingOperationId is not null || _cancelTarget is not null) && !NeedsRecovery;
    public int OperationProgress => Math.Clamp(_observedOperation?.Progress ?? 0, 0, 100);
    public bool IsOperationIndeterminate => _observedOperation?.Progress is null;

    private static LocalizedStatus OperationSummary(InstallationOperationDto operation) => LocalizedStatus.Format(
        "proxy_server.operation_state", operation.OperationId.ToString("D"),
        LocalizedStatus.Key("component_center.operation_state." + operation.State),
        LocalizedStatus.Key("installation.stage." + operation.Stage));

    /// <summary>What the Host measured about itself; there is nothing to show until it has answered once.</summary>
    public bool HasEnvironment => _installation?.Environment is not null;
    private RuntimeDependencyObservation? Fact(RuntimeDependencyKind kind) =>
        _installation?.Environment?.Observations.FirstOrDefault(o => o.Kind == kind);
    public LocalizedStatus EnvironmentPlatformText => LocalizedStatus.Literal(Fact(RuntimeDependencyKind.Platform)
        is { } platform ? platform.Measured ?? platform.Identity : "—");
    public LocalizedStatus EnvironmentServiceHostText => LocalizedStatus.Literal(Fact(RuntimeDependencyKind.ServiceHost)
        is { } service ? service.Measured ?? service.Identity : "—");
    public LocalizedStatus EnvironmentDiskSpaceText => Fact(RuntimeDependencyKind.DiskSpace)
        is { AvailableBytes: { } bytes } ? LocalizedStatus.Literal(Size(bytes)) : LocalizedStatus.Literal("—");
    public LocalizedStatus EnvironmentFileSystemText => Fact(RuntimeDependencyKind.FileSystem) is { } directory
        ? LocalizedStatus.Key("proxy_server.runtime.environment.state." + directory.State, directory.State.ToString())
        : LocalizedStatus.Literal("—");
    public LocalizedStatus EnvironmentKernelText => Fact(RuntimeDependencyKind.KernelFeature) is { } feature
        ? LocalizedStatus.Key("proxy_server.runtime.environment.ipv6." + feature.State, feature.State.ToString())
        : LocalizedStatus.Literal("—");
    /// <summary>
    /// Only the facts that would make an install impossible are called out. An absent IPv6 stack is not one of
    /// them: it only matters for a configuration that binds an IPv6 address, which the plan measures separately.
    /// </summary>
    public bool HasEnvironmentWarning => _installation?.Environment?.Observations.Any(o => o.Kind is
        RuntimeDependencyKind.Platform or RuntimeDependencyKind.ServiceHost or RuntimeDependencyKind.FileSystem
        && o.State != RuntimeDependencyState.Satisfied) == true;
    public LocalizedStatus EnvironmentWarning => LocalizedStatus.Join("\n",
        (_installation?.Environment?.Observations ?? []).Where(o => o.Kind is RuntimeDependencyKind.Platform
            or RuntimeDependencyKind.ServiceHost or RuntimeDependencyKind.FileSystem
            && o.State != RuntimeDependencyState.Satisfied)
        .Select(o => LocalizedStatus.Key("proxy_server.runtime.environment.warning." + o.Kind, o.Kind.ToString())));

    /// <summary>One line per unmet requirement of the refused plan, not a single opaque code.</summary>
    public bool HasDependencyBlock => _blockedDependencies is { Unmet.Count: > 0 };
    public LocalizedStatus DependencyText => LocalizedStatus.Join("\n",
        (_blockedDependencies?.Unmet ?? []).Select(Describe));

    private static LocalizedStatus Describe(RuntimeDependencyObservation observation) => observation switch
    {
        { State: RuntimeDependencyState.Unknown } => LocalizedStatus.Format(
            "proxy_server.runtime.dependency.unknown", observation.Kind.ToString()),
        { Kind: RuntimeDependencyKind.DiskSpace } => LocalizedStatus.Format("proxy_server.runtime.dependency.disk_space",
            Size(observation.AvailableBytes ?? 0), Size(observation.RequiredBytes ?? 0)),
        { Kind: RuntimeDependencyKind.Platform } => LocalizedStatus.Format("proxy_server.runtime.dependency.platform",
            observation.Identity, observation.Measured ?? "—"),
        { Kind: RuntimeDependencyKind.ServiceHost } => LocalizedStatus.Format("proxy_server.runtime.dependency.service_host",
            observation.Identity),
        { Kind: RuntimeDependencyKind.FileSystem } => LocalizedStatus.Key("proxy_server.runtime.dependency.file_system"),
        _ => LocalizedStatus.Format("proxy_server.runtime.dependency.kernel_feature", observation.Identity)
    };

    private static string Size(long bytes) => bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.#") + " GiB"
        : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.#") + " MiB"
        : (bytes / 1024d).ToString("0.#") + " KiB";

    public LocalizedStatus OwnershipText => _installation is null
        ? LocalizedStatus.Key("proxy_server.runtime.ownership.Unknown")
        : LocalizedStatus.Key("proxy_server.runtime.ownership." + _installation.Installation.Ownership, _installation.Installation.Ownership.ToString());
    public LocalizedStatus TargetText => _installation is null ? LocalizedStatus.Literal("—") : LocalizedStatus.Literal(
        _installation.Installation.Target.Os + " / " + _installation.Installation.Target.Architecture);
    public LocalizedStatus InstallModeText => _installation is null ? LocalizedStatus.Literal("—")
        : LocalizedStatus.Key("component_center.mode." + _installation.Installation.Target.InstallMode);
    public LocalizedStatus MeasuredVersionText => LocalizedStatus.Literal(_installation?.MeasuredVersion ?? "—");
    public LocalizedStatus ActiveRevisionText => LocalizedStatus.Literal(_installation?.ActiveRevisionId?.ToString("D") ?? "—");
    public LocalizedStatus PublishedConfigurationText => LocalizedStatus.Literal(_installation?.ConfigurationIdentity ?? "—");
    public LocalizedStatus InstalledReleasesText => _installation?.InstalledReleases is { Count: > 0 } installed
        ? LocalizedStatus.Literal(string.Join(" · ", installed.Select(r => r.MeasuredVersion)))
        : LocalizedStatus.Literal("—");
    public LocalizedStatus PendingText => _installation is null ? LocalizedStatus.Key("proxy_server.state.unknown")
        : _installation.PendingStage is { } stage
        ? LocalizedStatus.Format("proxy_server.runtime.pending", LocalizedStatus.Key("installation.stage." + stage), _installation.PendingOperationId?.ToString("D") ?? "—")
        : LocalizedStatus.Key("proxy_server.runtime.pending_none");
    public LocalizedStatus RuntimeProblemText => _installation?.ProblemCode is { } problem
        ? ProblemText(problem) : LocalizedStatus.Literal("");
    public LocalizedStatus SourceSummary => _catalog is null ? LocalizedStatus.Literal("")
        : LocalizedStatus.Format("proxy_server.runtime.source_summary", _catalog.Source.Identity.Publisher,
            _catalog.Source.Identity.TrustRevision, _catalog.Source.CatalogRevision);
    public LocalizedStatus CatalogText => _catalog is null ? LocalizedStatus.Key("proxy_server.runtime.catalog_absent")
        : _catalog.Catalog is { } catalog
            ? LocalizedStatus.Format("proxy_server.runtime.catalog_loaded", catalog.Releases.Count,
                catalog.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'"), catalog.Revision)
            : LocalizedStatus.Key("proxy_server.runtime.catalog_absent");
    public LocalizedStatus CatalogProblem => _catalog is null ? LocalizedStatus.Key("proxy_server.runtime.catalog_absent") : _refreshFailed
        ? LocalizedStatus.Key("proxy_server.runtime.refresh_failed")
        : _catalog?.ProblemCode is { } problem ? ProblemText(problem) : LocalizedStatus.Key("proxy_server.runtime.catalog_fresh_hint");
    public LocalizedStatus ReleaseCountText => LocalizedStatus.Format("proxy_server.runtime.release_count", Releases.Count);
    public bool HasRuntimeProblem => !RuntimeProblemText.IsEmpty;
    public bool HasPlanText => !PlanText.IsEmpty;
    public LocalizedStatus ActionText
    {
        get
        {
            if (SelectedRelease is not { } option) return LocalizedStatus.Key("proxy_server.runtime.select_release");
            var change = LocalizedStatus.Key("proxy_server.runtime.action." + (ChangeFor(option.Release)?.ToString() ?? "Unavailable"));
            return StageChange is null ? change : LocalizedStatus.Format("component_center.add_change", change);
        }
    }

    partial void OnSelectedSourceChanged(RuntimeSourceView? value)
    {
        NotifyDerived();
        if (_loading || value is null || !CanRead) return;
        _ = RunAsync(() => LoadCatalogAsync(refresh: false));
    }

    partial void OnIncludePrereleaseChanged(bool value)
    {
        if (_loading || SelectedSource is null || !CanRead) return;
        _ = RunAsync(() => LoadCatalogAsync(refresh: true));
    }

    partial void OnSelectedReleaseChanged(RuntimeReleaseOption? value) => NotifyDerived();

    partial void OnPlanTextChanged(LocalizedStatus value) => OnPropertyChanged(nameof(HasPlanText));

    /// <summary>The page re-reads Host state on entry; a running operation is never duplicated.</summary>
    public Task EnsureLoadedAsync() => IsBusy || _invalidated ? Task.CompletedTask : LoadAsync();

    [RelayCommand] private Task RefreshSourcesAsync() => RunAsync(LoadSourcesAsync);

    [RelayCommand] private Task RefreshCatalogAsync() => RunAsync(RefreshCatalogCoreAsync);

    [RelayCommand] private Task ActAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (SelectedRelease is not { Available: true } option || _catalog?.Catalog is not { } catalog
            || _installation is not { PendingOperationId: null } || NeedsRecovery) return;
        if (ChangeFor(option.Release) is not { } change) return;
        if (StageChange is { } stage)
        {
            if (stage(Draft(change, option.Release, keepData: true))) OutcomeText = LocalizedStatus.Key("component_center.staged");
            return;
        }
        await ExecuteAsync(change, option.Release, catalog.Revision, _catalog.Source.Identity.TrustRevision, keepData: true);
    });

    [RelayCommand] private Task UninstallKeepDataAsync() => UninstallAsync(keepData: true);

    [RelayCommand] private Task UninstallDeleteDataAsync() => UninstallAsync(keepData: false);

    /// <summary>Replays the Host's own compensation. It never re-prepares a revision or new credentials.</summary>
    [RelayCommand] private Task RetryRecoveryAsync() => RunAsync(() => RunElevatedAsync(async () =>
    {
        RequireManage();
        if (_recovery?.OperationId is not { } id) return;
        var receipt = await client.RetryRecoveryAsync(id, _lifetime.Token);
        OutcomeText = receipt.Outcome == RuntimeSwitchOutcome.Succeeded
            ? LocalizedStatus.Key("proxy_server.runtime.recovery_retried")
            : receipt.ProblemCode is { } problem ? ProblemText(problem)
            : LocalizedStatus.Key("proxy_server.runtime.recovery_still_required");
        await ReloadStateAsync();
    }));

    /// <summary>The emergency stop is available precisely when compensation cannot be trusted.</summary>
    [RelayCommand] private Task EmergencyStopAsync() => RunAsync(() => RunElevatedAsync(async () =>
    {
        RequireManage();
        if (_recovery?.OperationId is not { } id) return;
        _recovery = await client.StopRecoveryAsync(id, _lifetime.Token);
        OutcomeText = LocalizedStatus.Key("proxy_server.runtime.recovery_stopped");
        await ReloadStateAsync();
    }));

    [RelayCommand] private Task CloseRecoveryAsync() => RunAsync(() => RunElevatedAsync(async () =>
    {
        RequireManage();
        if (_recovery?.OperationId is not { } id) return;
        // A missed confirmation host must never turn into a silent close.
        var accepted = ConfirmAsync is { } confirm && await confirm(
            LocalizedStatus.Key("proxy_server.runtime.recovery_close_title"),
            LocalizedStatus.Key("proxy_server.runtime.recovery_close_warning"));
        if (!accepted) return;
        RequireManage();
        await client.CloseRecoveryAsync(id, _lifetime.Token);
        OutcomeText = LocalizedStatus.Key("proxy_server.runtime.recovery_closed");
        await ReloadStateAsync();
    }));

    private Task UninstallAsync(bool keepData) => RunAsync(async () =>
    {
        RequireManage();
        // Eligibility is re-checked without the busy flag, which the running command itself set.
        if (_installation is not { Installation.Ownership: RuntimeOwnership.Managed, PendingOperationId: null } || NeedsRecovery) return;
        if (StageChange is { } stage)
        {
            if (stage(Draft(RuntimeChange.Uninstall, null, keepData))) OutcomeText = LocalizedStatus.Key("component_center.staged");
            return;
        }
        // A missed confirmation host must never turn into a silent removal.
        var accepted = ConfirmAsync is { } confirm && await confirm(
            LocalizedStatus.Key(keepData ? "proxy_server.runtime.uninstall_title" : "proxy_server.runtime.uninstall_delete_title"),
            LocalizedStatus.Key(keepData ? "proxy_server.runtime.uninstall_warning" : "proxy_server.runtime.uninstall_delete_warning"));
        if (!accepted) return;
        await ExecuteAsync(RuntimeChange.Uninstall, null, _catalog?.Catalog?.Revision ?? 0,
            _catalog?.Source.Identity.TrustRevision ?? 0, keepData);
    });

    /// <summary>Re-resolves saved intent on the current host. Nothing in a draft grants execution permission.</summary>
    public async Task<bool> ApplyDraftAsync(ComponentChangeDraft draft)
    {
        var succeeded = false;
        await RunAsync(async () =>
        {
            RequireManage();
            await ReloadStateAsync();
            if (_installation is null || _installation.Installation.HostId != draft.HostId
                || _installation.Installation.InstallationId != draft.InstallationId
                || draft.RuntimeId != client.RuntimeId || draft.ComponentId != client.RuntimeId || NeedsRecovery
                || _installation.PendingOperationId is not null)
                throw new InvalidOperationException("component_center.draft_stale");
            if (draft.Change == RuntimeChange.Uninstall)
            {
                if (_installation.Installation.Ownership != RuntimeOwnership.Managed)
                    throw new InvalidOperationException("component_center.draft_stale");
                if (ConfirmAsync is not { } confirm || !await confirm(
                    LocalizedStatus.Key(draft.KeepData ? "proxy_server.runtime.uninstall_title" : "proxy_server.runtime.uninstall_delete_title"),
                    LocalizedStatus.Key(draft.KeepData ? "proxy_server.runtime.uninstall_warning" : "proxy_server.runtime.uninstall_delete_warning"))) return;
                _catalog = null;
                succeeded = await ExecuteAsync(RuntimeChange.Uninstall, null, 0, 0, draft.KeepData);
                return;
            }
            await LoadSourcesAsync();
            var source = Sources.SingleOrDefault(s => s.Identity.SourceId == draft.SourceId)
                ?? throw new InvalidOperationException("component_center.draft_stale");
            _loading = true;
            SelectedSource = source;
            IncludePrerelease = draft.AllowPrerelease;
            _loading = false;
            await LoadCatalogAsync(refresh: false, source);
            var option = Releases.SingleOrDefault(r => r.Release.ReleaseId == draft.ReleaseId && r.Available)
                ?? throw new InvalidOperationException("component_center.draft_stale");
            if (ChangeFor(option.Release) != draft.Change || option.Version != draft.Version)
                throw new InvalidOperationException("component_center.draft_stale");
            succeeded = await ExecuteAsync(draft.Change, option.Release, _catalog!.Catalog!.Revision,
                _catalog.Source.Identity.TrustRevision, draft.KeepData);
        });
        return succeeded;
    }

    /// <summary>
    /// A cancellation request, never an assumption. The Server records it and the Host decides whether the
    /// transaction was still before its switch checkpoint; the poll loop keeps waiting either way.
    /// </summary>
    [RelayCommand] private async Task CancelDownloadAsync()
    {
        SynchronizeAccess();
        if (!CanCancelDownload || _cancelTarget is not { } id) return;
        try
        {
            await client.CancelAsync(id, Guid.NewGuid().ToString("N"), _lifetime.Token);
            _cancelRequested = id;
            _operationCancellable = false;
            OperationText = LocalizedStatus.Key("proxy_server.runtime.cancel_requested");
            NotifyDerived();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or FormatException)
        { OutcomeText = ProblemText(ex.Message); }
    }

    private async Task<bool> ExecuteAsync(RuntimeChange change, RuntimeRelease? release, long catalogRevision, long trustRevision,
        bool keepData)
    {
        if (_installation is null) return false;
        var request = new RuntimePlanRequest(_installation.Installation.InstallationId, _installation.Installation.HostId,
            change, _installation.Installation.StateRevision, catalogRevision, trustRevision,
            release?.ReleaseId, Guid.NewGuid().ToString("N"), IncludePrerelease, keepData);
        var plan = await client.PlanAsync(_catalog?.Source.Identity.SourceId, request, _lifetime.Token);
        _blockedDependencies = plan.Plan is null ? plan.Dependencies : null;
        NotifyDerived();
        if (!plan.Allowed || plan.Plan is null)
        {
            PlanText = LocalizedStatus.Join("\n", plan.Problems.Select(ProblemText));
            OutcomeText = LocalizedStatus.Key("proxy_server.runtime.plan_blocked");
            return false;
        }
        PlanText = release is null ? LocalizedStatus.Key("proxy_server.runtime.removal_ready")
            : LocalizedStatus.Key("proxy_server.runtime.plan_ready", null, release.OriginalVersion);
        // Approval is bound to the frozen host proposal, after dependency resolution.
        if (ConfirmAsync is not { } approve || !await approve(
                LocalizedStatus.Key("component_center.preview_title"),
                LocalizedStatus.Format("component_center.preview", plan.Plan.Request.HostId,
                    plan.Plan.RuntimeId, LocalizedStatus.Key("proxy_server.runtime.change." + plan.Plan.Request.Change),
                    _installation.MeasuredVersion ?? "—", plan.Plan.Release?.OriginalVersion ?? "—",
                    plan.Plan.Release?.SourceId ?? "—", LocalizedStatus.Key(plan.Plan.Request.KeepData
                        ? "component_center.data_retained" : "component_center.data_deleted")))) return false;
        if (_invalidated) return false;
        var key = Guid.NewGuid().ToString("N");
        InstallationOperationDto? submitted = null;
        await RunElevatedAsync(async () =>
        {
            submitted = await client.StartAsync(Kind(change), new ManagedRuntimeInstallationRequest(true, plan.Plan), key, _lifetime.Token);
        });
        var operation = submitted ?? throw new InvalidOperationException("runtime.operation_failed");
        ValidateOperation(operation, operation.OperationId);
        OperationText = LocalizedStatus.Format("proxy_server.operation_pending", operation.OperationId.ToString("D"));
        _cancelTarget = operation.OperationId;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var current = await client.OperationAsync(operation.OperationId, _lifetime.Token)
                    ?? throw new InvalidOperationException("runtime.operation_failed");
                ValidateOperation(current, operation.OperationId);
                _observedOperation = current;
                OperationText = OperationSummary(current);
                _operationCancellable = current.Cancellable;
                NotifyDerived();
                // A cancellation the Host performed is an outcome, not a failure: say so and stop quietly.
                if (current.State == InstallationOperationState.Cancelled)
                {
                    await ReloadStateAsync();
                    PlanText = LocalizedStatus.Literal("");
                    OutcomeText = LocalizedStatus.Key("proxy_server.runtime.operation_cancelled");
                    return false;
                }
                if (current.State is InstallationOperationState.Failed or InstallationOperationState.Interrupted)
                    throw new InvalidOperationException(string.IsNullOrEmpty(current.ProblemCode)
                        ? "runtime.operation_failed" : current.ProblemCode);
                if (current.State is InstallationOperationState.Succeeded) break;
                if (attempt >= 300) throw new InvalidOperationException("runtime.operation_pending");
                await Task.Delay(1000, _lifetime.Token);
            }
        }
        finally { _cancelTarget = null; _operationCancellable = false; NotifyDerived(); }
        await ReloadStateAsync();
        PlanText = LocalizedStatus.Literal("");
        OutcomeText = LocalizedStatus.Key(change == RuntimeChange.Uninstall
            ? "proxy_server.runtime.uninstalled" : "proxy_server.runtime.action_done");
        return true;
    }

    /// <summary>Loads installation and sources once; the page is also reachable before anything is installed.</summary>
    public Task LoadAsync() => RunAsync(async () =>
    {
        RequireRead();
        await ReloadStateAsync();
        await ObserveActiveOperationAsync();
        await LoadSourcesAsync();
    });

    /// <summary>Reattaches to the host's existing ledger entry, never starts or retries a change.</summary>
    public Task RefreshActiveOperationAsync() => RunAsync(async () =>
    {
        RequireRead();
        var previous = _cancelTarget;
        _cancelTarget = null;
        _operationCancellable = false;
        await ReloadStateAsync();
        await ObserveActiveOperationAsync(previous);
    });

    private async Task ObserveActiveOperationAsync(Guid? previous = null)
    {
        _cancelTarget = null;
        _operationCancellable = false;
        _observedOperation = null;
        if (_installation is null || NeedsRecovery || (_installation.PendingOperationId ?? previous) is not { } id) return;
        var operation = await client.OperationAsync(id, _lifetime.Token)
            ?? throw new InvalidOperationException("runtime.operation_failed");
        RequireRead();
        ValidateOperation(operation, id);
        _observedOperation = operation;
        OperationText = OperationSummary(operation);
        if (operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
        {
            _cancelTarget = id;
            _operationCancellable = operation.Cancellable;
        }
        else
        {
            await ReloadStateAsync();
            OutcomeText = operation.State switch
            {
                InstallationOperationState.Succeeded => LocalizedStatus.Key("proxy_server.runtime.action_done"),
                InstallationOperationState.Cancelled => LocalizedStatus.Key("proxy_server.runtime.operation_cancelled"),
                _ => ProblemText(operation.ProblemCode ?? "runtime.operation_failed")
            };
        }
        NotifyDerived();
    }

    private void ValidateOperation(InstallationOperationDto operation, Guid expectedId)
    {
        var service = client.RuntimeId switch
        {
            "sing-box" => InstallationServiceId.SingBox,
            "mihomo" => InstallationServiceId.Mihomo,
            _ => throw new InvalidOperationException("runtime.request_invalid")
        };
        if (operation.OperationId != expectedId || operation.Service != service)
            throw new InvalidOperationException("runtime.request_invalid");
    }

    /// <summary>Re-reads Host state without re-entering the busy gate, so a finished command can refresh itself.</summary>
    private async Task ReloadStateAsync()
    {
        await LoadInstallationAsync();
        await LoadRecoveryAsync();
    }

    private async Task LoadInstallationAsync()
    {
        RequireRead();
        _installation = null; // A failed observation is unknown, never a stale installation authority.
        var observation = await client.InstallationAsync(_lifetime.Token);
        RequireRead();
        _installation = observation;
        RebuildReleases();
        NotifyDerived();
    }

    /// <summary>
    /// Recovery facts are asked for only while a transaction is unfinished, and the previous answer is never
    /// reused: after every recovery action the Host is asked again instead of the page assuming an outcome.
    /// </summary>
    private async Task LoadRecoveryAsync()
    {
        _recovery = null;
        if (NeedsRecovery) _recovery = await client.RecoveryAsync(_lifetime.Token);
        NotifyDerived();
    }

    private async Task LoadSourcesAsync()
    {
        RequireRead();
        var sources = await client.SourcesAsync(_lifetime.Token);
        RequireRead();
        var previous = SelectedSource?.Identity.SourceId;
        RuntimeSourceView? selection;
        _loading = true;
        try
        {
            Sources.Clear(); foreach (var source in sources.Where(s => s.RuntimeId == client.RuntimeId)) Sources.Add(source);
            selection = Sources.FirstOrDefault(s => s.Identity.SourceId == previous);
            if (selection is null && _installation?.Installation is { Ownership: RuntimeOwnership.Managed, ActiveReleaseId: { } active })
            {
                // A missing active release must never silently move an installed component to the first source.
                foreach (var source in Sources)
                {
                    var catalog = await client.CatalogAsync(source.Identity.SourceId, IncludePrerelease, _lifetime.Token);
                    if (catalog.Catalog?.Releases.Any(r => r.ReleaseId == active && r.SourceId == source.Identity.SourceId) == true)
                    {
                        if (selection is not null) throw new InvalidOperationException("component_center.source_ambiguous");
                        selection = source;
                    }
                }
            }
            else if (selection is null && _installation?.Installation.Ownership == RuntimeOwnership.Absent)
                selection = Sources.FirstOrDefault();
            SelectedSource = selection;
        }
        finally { _loading = false; }
        NotifyDerived();
        if (SelectedSource is { } selected) await LoadCatalogAsync(refresh: false, selected);
        else
        {
            _catalog = null;
            RebuildReleases();
            OutcomeText = LocalizedStatus.Key("component_center.source_select");
            NotifyDerived();
        }
    }

    private Task LoadCatalogAsync(bool refresh) => SelectedSource is { } source
        ? LoadCatalogAsync(refresh, source) : Task.CompletedTask;

    /// <summary>
    /// An explicit refresh that fails keeps the cached list and says it failed. Reporting a failed refresh as
    /// "already the newest" is exactly what the design forbids, so the failure is shown where the catalog is.
    /// </summary>
    private async Task RefreshCatalogCoreAsync()
    {
        RequireManage();
        if (SelectedSource is not { } source) return;
        try
        {
            _catalog = await client.RefreshAsync(source.Identity.SourceId, IncludePrerelease, _lifetime.Token);
            _refreshFailed = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or FormatException)
        {
            _refreshFailed = true;
            OutcomeText = ProblemText(ex.Message);
        }
        RebuildReleases();
        NotifyDerived();
    }

    private async Task LoadCatalogAsync(bool refresh, RuntimeSourceView source)
    {
        RequireRead();
        _catalog = refresh
            ? await client.RefreshAsync(source.Identity.SourceId, IncludePrerelease, _lifetime.Token)
            : await client.CatalogAsync(source.Identity.SourceId, IncludePrerelease, _lifetime.Token);
        RequireRead();
        _refreshFailed = false;
        RebuildReleases();
        NotifyDerived();
    }

    private void RebuildReleases()
    {
        var previous = SelectedRelease?.Release.ReleaseId;
        var installed = _installation?.InstalledReleases?.Select(r => r.ReleaseId).ToHashSet(StringComparer.Ordinal) ?? [];
        var target = _installation?.Installation.Target;
        _loading = true;
        Releases.Clear();
        if (_catalog?.Catalog is { } catalog)
        {
            foreach (var release in catalog.Releases.Where(r => r.RuntimeId == client.RuntimeId)
                .OrderByDescending(r => RuntimeSemanticVersion.TryParse(r.SortVersion, out var version) ? version : null)
                .ThenBy(r => r.ReleaseId, StringComparer.Ordinal))
            {
                var blocked = Blocked(release, target);
                if (blocked.IsEmpty && catalog.ExpiresAt <= DateTimeOffset.UtcNow) blocked = ProblemText("runtime.catalog_expired");
                Releases.Add(new RuntimeReleaseOption(release, installed.Contains(release.ReleaseId), blocked));
            }
        }
        SelectedRelease = Releases.FirstOrDefault(r => r.Release.ReleaseId == previous)
            ?? Releases.FirstOrDefault(r => r.Available && r.Release.ReleaseId == _installation?.Installation.ActiveReleaseId)
            ?? Releases.FirstOrDefault(r => r.Available);
        _loading = false;
    }

    /// <summary>Display-side reasons only; the Host plan and the Helper remain authoritative.</summary>
    private static LocalizedStatus Blocked(RuntimeRelease release, RuntimeTarget? target)
    {
        if (!release.ConfigurationSupported) return LocalizedStatus.Key("proxy_server.runtime_problem.configuration_unsupported");
        if (release.Trust != RuntimeTrust.Confirmed)
            return LocalizedStatus.Key("proxy_server.runtime_problem.release_untrusted");
        if (target is not null && release.Target != target)
            return LocalizedStatus.Key("proxy_server.runtime_problem.target_unsupported");
        return LocalizedStatus.Literal("");
    }

    /// <summary>
    /// Repair only targets the active release, install only applies when nothing is owned, and any other
    /// stored release is a rollback target. Updates require strictly greater SemVer precedence;
    /// unknown schemes and unstored older releases are not represented as upgrades.
    /// </summary>
    private RuntimeChange? ChangeFor(RuntimeRelease release)
    {
        if (_installation is null) return null;
        if (_installation.Installation.Ownership == RuntimeOwnership.Managed
            && release.ReleaseId == _installation.Installation.ActiveReleaseId) return RuntimeChange.Repair;
        if (_installation.Installation.Ownership == RuntimeOwnership.Absent) return RuntimeChange.Install;
        if (_installation.Installation.Ownership != RuntimeOwnership.Managed) return null;
        return _installation.InstalledReleases?.Any(r => r.ReleaseId == release.ReleaseId) == true
            ? RuntimeChange.Rollback : IsNewer(release) ? RuntimeChange.Update : null;
    }

    private static InstallationOperationKind Kind(RuntimeChange change) => change switch
    {
        RuntimeChange.Install => InstallationOperationKind.Install,
        RuntimeChange.Update => InstallationOperationKind.Upgrade,
        RuntimeChange.Repair => InstallationOperationKind.Repair,
        RuntimeChange.Rollback => InstallationOperationKind.Rollback,
        RuntimeChange.Uninstall => InstallationOperationKind.Uninstall,
        _ => throw new InvalidOperationException("runtime.action_invalid")
    };

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || _invalidated) return;
        IsBusy = true; OutcomeText = LocalizedStatus.Literal("");
        NotifyDerived();
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or FormatException)
        { OutcomeText = ProblemText(ex.Message); }
        finally { IsBusy = false; NotifyDerived(); }
    }

    private void RequireRead()
    {
        SynchronizeAccess();
        if (!CanRead || _invalidated) throw new InvalidOperationException("proxy_server.permission_denied");
    }
    partial void OnCanReadChanged(bool value)
    {
        if (!value) { CanManage = false; ClearObservations(); }
        NotifyDerived();
    }
    private void ClearObservations()
    {
        _cancelTarget = null; _cancelRequested = null; _operationCancellable = false; _observedOperation = null;
        _installation = null; _recovery = null; _catalog = null;
        _loading = true;
        try { Sources.Clear(); Releases.Clear(); SelectedSource = null; SelectedRelease = null; }
        finally { _loading = false; }
    }
    private void RequireManage()
    {
        RequireRead();
        if (!CanManage) throw new InvalidOperationException("proxy_server.permission_denied");
    }

    /// <summary>
    /// The Server keeps every privileged mutation behind a structured elevation grant. Prompting here turns a
    /// denied grant into a visible decision instead of a silent no-op, and the action is retried exactly once.
    /// </summary>
    private async Task RunElevatedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (HttpRequestException ex) when (ex.Message == "elevation-required")
        {
            var password = PasswordPromptAsync is { } prompt ? await prompt() : null;
            RequireManage();
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("runtime.elevation_cancelled");
            if (!await client.ElevateAsync(password, _lifetime.Token))
                throw new InvalidOperationException("proxy_server.permission_denied");
            await action();
        }
    }

    internal static LocalizedStatus ProblemText(string code) => code.StartsWith("component_center.", StringComparison.Ordinal)
        ? LocalizedStatus.Key(code) : LocalizedStatus.Key(
        "proxy_server.runtime_problem." + code.Replace("runtime.", "", StringComparison.Ordinal)
            .Replace("proxy_server.", "", StringComparison.Ordinal), code);

    public void InvalidateSession()
    {
        if (_invalidated) return;
        _invalidated = true; CanRead = false; CanManage = false; ClearObservations();
        _lifetime.Cancel(); OutcomeText = LocalizedStatus.Key("proxy_server.session_changed");
        NotifyDerived();
    }

    public void Dispose() { InvalidateSession(); _lifetime.Dispose(); }
}
