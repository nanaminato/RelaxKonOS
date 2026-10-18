using System.Collections.ObjectModel;
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
    private RuntimeCatalogSnapshot? _catalog;
    private bool _invalidated;
    private bool _loading;
    private bool _refreshFailed;

    private static readonly string[] DerivedProperties =
    [
        nameof(HasSources), nameof(HasReleases), nameof(HasSource), nameof(CanAct), nameof(CanRefreshCatalog),
        nameof(OwnershipText), nameof(TargetText), nameof(MeasuredVersionText), nameof(ActiveRevisionText),
        nameof(InstalledReleasesText), nameof(PendingText), nameof(RuntimeProblemText), nameof(PublishedConfigurationText),
        nameof(SourceSummary), nameof(CatalogText), nameof(CatalogProblem), nameof(ActionText), nameof(ShowPrereleaseToggle),
        nameof(ReleaseCountText), nameof(HasRuntimeProblem), nameof(CanUninstall), nameof(NeedsRecovery), nameof(RecoveryText)
    ];

    private void NotifyDerived() { foreach (var name in DerivedProperties) OnPropertyChanged(name); }

    [ObservableProperty] private bool canManage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool includePrerelease;
    [ObservableProperty] private LocalizedStatus outcomeText = LocalizedStatus.Literal("");
    [ObservableProperty] private LocalizedStatus operationText = LocalizedStatus.Literal("");
    [ObservableProperty] private LocalizedStatus planText = LocalizedStatus.Literal("");
    [ObservableProperty] private RuntimeSourceView? selectedSource;
    [ObservableProperty] private RuntimeReleaseOption? selectedRelease;

    /// <summary>Without a confirm host a destructive removal does nothing, exactly like the publication commands.</summary>
    public Func<LocalizedStatus, LocalizedStatus, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<RuntimeSourceView> Sources { get; } = [];
    public ObservableCollection<RuntimeReleaseOption> Releases { get; } = [];

    public bool HasSources => Sources.Count > 0;
    public bool HasSource => _catalog is not null;
    public bool HasReleases => Releases.Count > 0;
    public bool CanRefreshCatalog => CanManage && !IsBusy && !_invalidated && SelectedSource is not null;
    public bool CanAct => CanManage && !IsBusy && !_invalidated && !NeedsRecovery
        && SelectedRelease is { Available: true } && _installation is not null;
    public bool CanUninstall => CanManage && !IsBusy && !_invalidated && !NeedsRecovery
        && _installation is { Installation.Ownership: RuntimeOwnership.Managed };
    public bool ShowPrereleaseToggle => true;

    /// <summary>
    /// An interrupted operation or a recorded compensation failure is a recovery state: the Host keeps rejecting
    /// new lifecycle changes until its own recovery entry runs, so the page must say so instead of offering actions.
    /// </summary>
    public bool NeedsRecovery => _installation is { Installation.RecoveryRequired: true }
        or { PendingOperationId: not null, PendingStage: null };
    public LocalizedStatus RecoveryText => NeedsRecovery
        ? LocalizedStatus.Key("proxy_server.runtime.recovery_required") : LocalizedStatus.Literal("");

    public LocalizedStatus OwnershipText => _installation is null
        ? LocalizedStatus.Key("proxy_server.runtime.ownership.unknown")
        : LocalizedStatus.Key("proxy_server.runtime.ownership." + _installation.Installation.Ownership, _installation.Installation.Ownership.ToString());
    public LocalizedStatus TargetText => _installation is null ? LocalizedStatus.Literal("—") : LocalizedStatus.Literal(
        _installation.Installation.Target.Os + " / " + _installation.Installation.Target.Architecture);
    public LocalizedStatus MeasuredVersionText => LocalizedStatus.Literal(_installation?.MeasuredVersion ?? "—");
    public LocalizedStatus ActiveRevisionText => LocalizedStatus.Literal(_installation?.ActiveRevisionId?.ToString("D") ?? "—");
    public LocalizedStatus PublishedConfigurationText => LocalizedStatus.Literal(_installation?.ConfigurationIdentity ?? "—");
    public LocalizedStatus InstalledReleasesText => _installation?.InstalledReleases is { Count: > 0 } installed
        ? LocalizedStatus.Literal(string.Join(" · ", installed.Select(r => r.MeasuredVersion)))
        : LocalizedStatus.Literal("—");
    public LocalizedStatus PendingText => _installation?.PendingStage is { } stage
        ? LocalizedStatus.Format("proxy_server.runtime.pending", stage.ToString(), _installation.PendingOperationId?.ToString("D") ?? "—")
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
    public LocalizedStatus CatalogProblem => _refreshFailed
        ? LocalizedStatus.Key("proxy_server.runtime.refresh_failed")
        : _catalog?.ProblemCode is { } problem ? ProblemText(problem) : LocalizedStatus.Key("proxy_server.runtime.catalog_fresh_hint");
    public LocalizedStatus ReleaseCountText => LocalizedStatus.Format("proxy_server.runtime.release_count", Releases.Count);
    public bool HasRuntimeProblem => !RuntimeProblemText.IsEmpty;
    public bool HasPlanText => !PlanText.IsEmpty;
    public LocalizedStatus ActionText => SelectedRelease is not { } option ? LocalizedStatus.Key("proxy_server.runtime.select_release")
        : LocalizedStatus.Key("proxy_server.runtime.action." + (ChangeFor(option.Release)?.ToString() ?? "Unavailable"),
            (ChangeFor(option.Release)?.ToString() ?? "Unavailable"));

    partial void OnSelectedSourceChanged(RuntimeSourceView? value)
    {
        NotifyDerived();
        if (_loading || value is null) return;
        _ = RunAsync(() => LoadCatalogAsync(refresh: false));
    }

    partial void OnIncludePrereleaseChanged(bool value)
    {
        if (_loading || SelectedSource is null) return;
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
        if (SelectedRelease is not { Available: true } option || _catalog?.Catalog is not { } catalog) return;
        if (ChangeFor(option.Release) is not { } change) return;
        await ExecuteAsync(change, option.Release, catalog.Revision, _catalog.Source.Identity.TrustRevision, keepData: true);
    });

    [RelayCommand] private Task UninstallKeepDataAsync() => UninstallAsync(keepData: true);

    [RelayCommand] private Task UninstallDeleteDataAsync() => UninstallAsync(keepData: false);

    private Task UninstallAsync(bool keepData) => RunAsync(async () =>
    {
        RequireManage();
        // Eligibility is re-checked without the busy flag, which the running command itself set.
        if (_installation is not { Installation.Ownership: RuntimeOwnership.Managed }) return;
        // A missed confirmation host must never turn into a silent removal.
        var accepted = ConfirmAsync is { } confirm && await confirm(
            LocalizedStatus.Key(keepData ? "proxy_server.runtime.uninstall_title" : "proxy_server.runtime.uninstall_delete_title"),
            LocalizedStatus.Key(keepData ? "proxy_server.runtime.uninstall_warning" : "proxy_server.runtime.uninstall_delete_warning"));
        if (!accepted) return;
        await ExecuteAsync(RuntimeChange.Uninstall, null, _catalog?.Catalog?.Revision ?? 0,
            _catalog?.Source.Identity.TrustRevision ?? 0, keepData);
    });

    private async Task ExecuteAsync(RuntimeChange change, RuntimeRelease? release, long catalogRevision, long trustRevision,
        bool keepData)
    {
        if (_installation is null) return;
        var request = new RuntimePlanRequest(_installation.Installation.InstallationId, _installation.Installation.HostId,
            change, _installation.Installation.StateRevision, catalogRevision, trustRevision,
            release?.ReleaseId, Guid.NewGuid().ToString("N"), IncludePrerelease, keepData);
        var plan = await client.PlanAsync(_catalog?.Source.Identity.SourceId, request, _lifetime.Token);
        if (plan.Plan is null)
        {
            PlanText = LocalizedStatus.Join("\n", plan.Problems.Select(ProblemText));
            OutcomeText = LocalizedStatus.Key("proxy_server.runtime.plan_blocked");
            return;
        }
        PlanText = release is null ? LocalizedStatus.Key("proxy_server.runtime.removal_ready")
            : LocalizedStatus.Key("proxy_server.runtime.plan_ready", null, release.OriginalVersion);
        var operation = await client.StartAsync(Kind(change), new ManagedRuntimeInstallationRequest(true, plan.Plan),
            Guid.NewGuid().ToString("N"), _lifetime.Token)
            ?? throw new InvalidOperationException("runtime.operation_failed");
        OperationText = LocalizedStatus.Format("proxy_server.operation_pending", operation.OperationId.ToString("D"));
        for (var attempt = 0; ; attempt++)
        {
            var current = await client.OperationAsync(operation.OperationId, _lifetime.Token)
                ?? throw new InvalidOperationException("runtime.operation_failed");
            OperationText = LocalizedStatus.Format("proxy_server.operation_state",
                current.OperationId.ToString("D"), current.State, current.Stage);
            if (current.State is InstallationOperationState.Failed or InstallationOperationState.Cancelled
                or InstallationOperationState.Interrupted)
                throw new InvalidOperationException(string.IsNullOrEmpty(current.ProblemCode)
                    ? "runtime.operation_failed" : current.ProblemCode);
            if (current.State is InstallationOperationState.Succeeded) break;
            if (attempt >= 300) throw new InvalidOperationException("runtime.operation_pending");
            await Task.Delay(1000, _lifetime.Token);
        }
        await LoadAsync();
        PlanText = LocalizedStatus.Literal("");
        OutcomeText = LocalizedStatus.Key(change == RuntimeChange.Uninstall
            ? "proxy_server.runtime.uninstalled" : "proxy_server.runtime.action_done");
    }

    /// <summary>Loads installation and sources once; the page is also reachable before anything is installed.</summary>
    public Task LoadAsync() => RunAsync(async () =>
    {
        await LoadInstallationAsync();
        await LoadSourcesAsync();
    });

    private async Task LoadInstallationAsync()
    {
        _installation = await client.InstallationAsync(_lifetime.Token);
        RebuildReleases();
        NotifyDerived();
    }

    private async Task LoadSourcesAsync()
    {
        var sources = await client.SourcesAsync(_lifetime.Token);
        var previous = SelectedSource?.Identity.SourceId;
        _loading = true;
        Sources.Clear(); foreach (var source in sources) Sources.Add(source);
        SelectedSource = Sources.FirstOrDefault(s => s.Identity.SourceId == previous) ?? Sources.FirstOrDefault();
        _loading = false;
        NotifyDerived();
        if (SelectedSource is { } selected) await LoadCatalogAsync(refresh: false, selected);
    }

    private Task LoadCatalogAsync(bool refresh) => SelectedSource is { } source
        ? LoadCatalogAsync(refresh, source) : Task.CompletedTask;

    /// <summary>
    /// An explicit refresh that fails keeps the cached list and says it failed. Reporting a failed refresh as
    /// "already the newest" is exactly what the design forbids, so the failure is shown where the catalog is.
    /// </summary>
    private async Task RefreshCatalogCoreAsync()
    {
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
        _catalog = refresh
            ? await client.RefreshAsync(source.Identity.SourceId, IncludePrerelease, _lifetime.Token)
            : await client.CatalogAsync(source.Identity.SourceId, IncludePrerelease, _lifetime.Token);
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
            foreach (var release in catalog.Releases.OrderByDescending(r => r.PublishedAt).ThenBy(r => r.OriginalVersion, StringComparer.Ordinal))
                Releases.Add(new RuntimeReleaseOption(release, installed.Contains(release.ReleaseId), Blocked(release, target)));
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
    /// stored release is a rollback target. An unknown older release is offered as an update and rejected
    /// by the Host plan instead of being silently treated as a downgrade.
    /// </summary>
    private RuntimeChange? ChangeFor(RuntimeRelease release)
    {
        if (_installation is null) return null;
        if (release.ReleaseId == _installation.Installation.ActiveReleaseId) return RuntimeChange.Repair;
        if (_installation.Installation.Ownership == RuntimeOwnership.Absent) return RuntimeChange.Install;
        if (_installation.Installation.Ownership != RuntimeOwnership.Managed) return null;
        return _installation.InstalledReleases?.Any(r => r.ReleaseId == release.ReleaseId) == true
            ? RuntimeChange.Rollback : RuntimeChange.Update;
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

    private void RequireManage()
    {
        if (!CanManage) throw new InvalidOperationException("proxy_server.permission_denied");
    }

    internal static LocalizedStatus ProblemText(string code) => LocalizedStatus.Key(
        "proxy_server.runtime_problem." + code.Replace("runtime.", "", StringComparison.Ordinal)
            .Replace("proxy_server.", "", StringComparison.Ordinal), code);

    public void InvalidateSession()
    {
        _invalidated = true; CanManage = false;
        _lifetime.Cancel(); OutcomeText = LocalizedStatus.Key("proxy_server.session_changed");
        NotifyDerived();
    }

    public void Dispose() { InvalidateSession(); _lifetime.Dispose(); }
}
