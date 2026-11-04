using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>One selectable catalog row. Presentation lives here so the list template
/// stays a statement of facts instead of repeating server-side enum names.</summary>
public sealed partial class ServerModuleSelection(ServerModuleDto fact, Action changed) : ObservableObject
{
    public ServerModuleDto Fact { get; } = fact;
    [ObservableProperty] private bool isSelected;
    partial void OnIsSelectedChanged(bool value) => changed();

    public LocalizedStatus Title => ServerModulesViewModel.ModuleDisplayName(Fact);
    public string Id => Fact.Id;
    public string VersionText => ServerModulesViewModel.VersionOf(Fact);
    public LocalizedStatus StateText => ServerModulesViewModel.DescribeModule(Fact).Text;
    public bool ToneOk => ServerModulesViewModel.DescribeModule(Fact).Tone == "ok";
    public bool ToneWarn => ServerModulesViewModel.DescribeModule(Fact).Tone == "warn";
    public bool ToneBad => ServerModulesViewModel.DescribeModule(Fact).Tone == "bad";
    public bool ToneIdle => ServerModulesViewModel.DescribeModule(Fact).Tone == "idle";
    public bool NeedsRestart => Fact.RestartRequired;
}

public sealed record ServerModulePolicyChoice(string Value)
{
    public LocalizedStatus Label => LocalizedStatus.Key("server_modules.strategy." + Value, Value);
}

public enum ServerModuleFilter { All, Enabled, Disabled, PendingRestart }

public sealed record ServerModuleFilterChoice(ServerModuleFilter Filter, LocalizedStatus Label);

public enum ServerModulesTab { Modules, Packages, Security }

public sealed record ServerModulesTabItem(ServerModulesTab Tab, LocalizedStatus Label);

/// <summary>A single action the interface may preview. <see cref="Allowed"/> mirrors the
/// command's own CanExecute so a blocked action keeps its reason instead of disappearing.</summary>
public sealed record ServerModuleActionRow(
    ServerModuleAction Action, LocalizedStatus Label, LocalizedStatus Hint, bool Allowed, ICommand Command)
{
    public bool HasHint => !Hint.IsEmpty;
}

public sealed record ServerModuleHealthRow(ServerModuleHealthModuleDto Fact)
{
    public string ModuleId => Fact.ModuleId;
    public string VersionText => Fact.Version ?? "—";
    public LocalizedStatus EnabledText => LocalizedStatus.Key(Fact.Enabled
        ? "server_modules.health.module_enabled" : "server_modules.health.module_disabled");
    public LocalizedStatus StatusText => ServerModulesViewModel.StatusLabel(Fact.Status);
    public bool HasProblem => !string.IsNullOrWhiteSpace(Fact.ProblemCode);
    public LocalizedStatus ProblemText => ServerModulesViewModel.ExplainProblem(Fact.ProblemCode);
    public bool ToneOk => !HasProblem;
    public bool ToneBad => HasProblem;
}

public sealed record ServerPackageOperationRow(ServerPackageOperationDto Fact)
{
    public LocalizedStatus Title => LocalizedStatus.Format("server_modules.package.operation_row",
        ServerModulesViewModel.PackageActionText(Fact.Action), Fact.ModuleId, Fact.Version);
    public LocalizedStatus StateText => ServerModulesViewModel.StatusLabel(Fact.State);
    public bool HasProblem => !string.IsNullOrWhiteSpace(Fact.ProblemCode);
    public LocalizedStatus ProblemText => ServerModulesViewModel.ExplainProblem(Fact.ProblemCode);
    public string TimeText => Fact.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public bool ToneOk => !HasProblem && Fact.State is "succeeded" or "applied";
    public bool ToneWarn => !ToneOk && !ToneBad;
    public bool ToneBad => HasProblem || Fact.State is "failed" or "interrupted";
}

public sealed record ServerModuleStoreSourceRow(ServerModuleStoreSourceDto Fact)
{
    public string SourceId => Fact.SourceId;
    public LocalizedStatus StatusText => ServerModulesViewModel.StoreSourceStatus(Fact.Status);
    public LocalizedStatus CountText => LocalizedStatus.Format("server_modules.store.releases",
        Fact.ReleaseCount, Fact.RevokedCount);
    public bool HasProblem => !string.IsNullOrWhiteSpace(Fact.ProblemCode);
    public LocalizedStatus ProblemText => ServerModulesViewModel.ExplainProblem(Fact.ProblemCode);
}

/// <summary>
/// Server-module management. The page separates four questions that used to share one raw text
/// block: what is installed, what the current process runs (residency), what may be changed, and
/// how a frozen plan will be submitted. Every command still goes through the same frozen-plan
/// protocol; this type owns presentation and the exact same recovery bookkeeping as before.
/// </summary>
public sealed partial class ServerModulesViewModel(ServerModuleManagementClient client,
    ServerModuleReferenceStore? references = null, string? connectionId = null, Guid actor = default) : ObservableObject
{
    private static readonly ServerModuleFilterChoice[] FilterItems =
    [
        new(ServerModuleFilter.All, LocalizedStatus.Key("server_modules.filter.all")),
        new(ServerModuleFilter.Enabled, LocalizedStatus.Key("server_modules.filter.enabled")),
        new(ServerModuleFilter.Disabled, LocalizedStatus.Key("server_modules.filter.disabled")),
        new(ServerModuleFilter.PendingRestart, LocalizedStatus.Key("server_modules.filter.pending")),
    ];
    private static readonly ServerModulesTabItem[] TabItems =
    [
        new(ServerModulesTab.Modules, LocalizedStatus.Key("server_modules.tab.modules")),
        new(ServerModulesTab.Packages, LocalizedStatus.Key("server_modules.tab.packages")),
        new(ServerModulesTab.Security, LocalizedStatus.Key("server_modules.tab.security")),
    ];

    public IReadOnlyList<ServerModuleFilterChoice> Filters => FilterItems;
    public IReadOnlyList<ServerModulesTabItem> Tabs => TabItems;
    /// <summary>Package, store and security surfaces are manager-only on the server. A read-only
    /// account sees the catalog facts, not three tabs whose actions it cannot use.</summary>
    public IReadOnlyList<ServerModulesTabItem> VisibleTabs => IsReadOnly ? [TabItems[0]] : TabItems;
    [ObservableProperty] private ServerModuleFilterChoice selectedFilter = FilterItems[0];
    [ObservableProperty] private ServerModulesTabItem? selectedTab = TabItems[0];
    partial void OnSelectedFilterChanged(ServerModuleFilterChoice value)
    {
        OnPropertyChanged(nameof(VisibleModules));
        OnPropertyChanged(nameof(HasVisibleModules));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(ShowListEmpty));
        OnPropertyChanged(nameof(ListEmptyText));
        SyncSelectedRow();
    }
    partial void OnSelectedTabChanged(ServerModulesTabItem? value)
    {
        OnPropertyChanged(nameof(IsModulesTab));
        OnPropertyChanged(nameof(IsPackagesTab));
        OnPropertyChanged(nameof(IsSecurityTab));
    }
    public bool IsModulesTab => SelectedTab?.Tab == ServerModulesTab.Modules;
    public bool IsPackagesTab => SelectedTab?.Tab == ServerModulesTab.Packages;
    public bool IsSecurityTab => SelectedTab?.Tab == ServerModulesTab.Security;

    [ObservableProperty] private string privilegePlanId = "";
    [ObservableProperty] private string privilegeAdministrator = "";
    [ObservableProperty] private string privilegePassword = "";
    [ObservableProperty] private string privilegeText = "";
    [ObservableProperty] private ModulePrivilegePlan? privilegePlan;
    partial void OnPrivilegePlanIdChanged(string value)
    {
        PrivilegePlan = null; PrivilegeText = "";
        OnPropertyChanged(nameof(CanLoadPrivilege));
        LoadPrivilegeCommand.NotifyCanExecuteChanged();
    }
    partial void OnPrivilegePlanChanged(ModulePrivilegePlan? value)
    {
        OnPropertyChanged(nameof(HasPrivilegePlan));
        OnPropertyChanged(nameof(CanApprovePrivilege));
        OnPropertyChanged(nameof(CanCancelPrivilege));
        ApprovePrivilegeCommand.NotifyCanExecuteChanged();
        CancelPrivilegeCommand.NotifyCanExecuteChanged();
    }
    public bool HasPrivilegePlan => PrivilegePlan is not null;
    public bool CanLoadPrivilege => !Busy && CanManage && !string.IsNullOrWhiteSpace(PrivilegePlanId);
    public bool CanApprovePrivilege => !Busy && CanManage && PrivilegePlan is { State: "AwaitingConfirmation" } plan
        && plan.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanCancelPrivilege => !Busy && CanManage && PrivilegePlan is not null;

    [RelayCommand(CanExecute = nameof(CanLoadPrivilege))] private Task LoadPrivilegeAsync() => Run(async () =>
    {
        if (!Guid.TryParse(PrivilegePlanId, out var id)) throw new InvalidOperationException("module.privilege_plan_not_found");
        var plan = await client.PrivilegePlanAsync(id);
        PrivilegePlan = plan;
        PrivilegeText = $"{plan.ModuleId} · {plan.Version} · {plan.PublisherId}\n{plan.ArtifactSha256}\n{plan.Kind} · {plan.Action} · {plan.Target}\n{plan.Reason}\n"
            + (plan.Package is { } package ? DescribeFrozenPackage(package) + "\n" : "")
            + (plan.File is { } file ? DescribeFrozenFile(file) + "\n" : "")
            + (plan.Data is { } data ? DescribeFrozenData(data) + "\n" : "")
            + (plan.Application is { } application ? DescribeFrozenApplication(application) + "\n" : "")
            + string.Join("\n", plan.Effects.Concat(plan.Warnings)) + $"\n{plan.State} · {plan.ExpiresAt}";
    });
    /// <summary>Show exactly what the Host froze. The dependency closure is part of the confirmed identity,
    /// so it is listed rather than summarized as a count.</summary>
    private static string DescribeFrozenPackage(ModulePackagePlanIdentity package) =>
        $"package: {package.Package} {package.Version} {package.Architecture}\n"
        + $"source: {package.SourceRef}\nrepository: {package.Repository}\n"
        + $"artifact: {package.ArtifactFileName} sha256:{package.Sha256} ({package.Bytes} bytes)\n"
        + $"dependencies ({package.Dependencies.Length}): " + string.Join(", ", package.Dependencies.Select(dependency => dependency.FileName));
    /// <summary>The content body is not part of the plan; only the path and the exact before/after state
    /// are displayed so the user never confirms hidden bytes.</summary>
    private static string DescribeFrozenFile(ModuleFilePlanIdentity file) =>
        $"file: {file.Path} ({file.Action})\nroot: {file.Root}\n"
        + $"before: {file.BeforeKind} {FormatDigest(file.BeforeSha256)} ({file.BeforeBytes} bytes)\n"
        + $"after: {file.AfterKind} {FormatDigest(file.AfterSha256)} ({file.AfterBytes} bytes) mode:{Convert.ToString(file.Mode, 8)}";
    /// <summary>A data plan carries a structural digest of the exact declared root, never file names or
    /// content. The backup reference is an opaque module-scoped name, not a path on this host.</summary>
    private static string DescribeFrozenData(ModuleDataPlanIdentity data) =>
        $"data: {data.Root} ({data.Action})\nbefore: {data.BeforeKind} {data.BeforeFiles} files {data.BeforeBytes} bytes\n"
        + $"digest: {data.BeforeDigest}" + (data.BackupRef.Length > 0 ? $"\nbackup: {data.BackupRef}" : "");
    private static string FormatDigest(string digest) => digest.Length == 0 ? "none" : "sha256:" + digest;
    /// <summary>The database password and its path are never part of the plan. Only the instance,
    /// account, server version and opaque secret revision are displayed.</summary>
    private static string DescribeFrozenApplication(ModuleApplicationPlanIdentity application) =>
        $"database: {application.Instance} ({application.Protocol}, {application.Action})\n"
        + $"account: {application.Account}@{application.AccountHost}\n"
        + $"server: {application.ServerVersion}\nsecret-revision: {application.SecretRevision}";
    [RelayCommand(CanExecute = nameof(CanApprovePrivilege))] private Task ApprovePrivilegeAsync() => Run(async () =>
    {
        if (PrivilegePlan is not { State: "AwaitingConfirmation" } plan || plan.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("module.privilege_plan_expired");
        try
        {
            var result = await client.ApprovePrivilegeAsync(plan.PlanId,
                new(plan.PlanHash, plan.ConfirmationToken, PrivilegeAdministrator, PrivilegePassword));
            PrivilegeText += $"\n{result.State} · {result.ProblemCode}";
            PrivilegePlan = null;
        }
        finally { PrivilegePassword = ""; }
    });
    [RelayCommand(CanExecute = nameof(CanCancelPrivilege))] private Task CancelPrivilegeAsync() => Run(async () =>
    {
        if (PrivilegePlan is not { } plan) return;
        var result = await client.CancelPrivilegeAsync(plan.PlanId);
        PrivilegeText += $"\n{result.State}";
        PrivilegePlan = null;
        PrivilegePassword = "";
    });

    [ObservableProperty] private IReadOnlyList<ServerModuleDto> modules = [];
    [ObservableProperty] private ServerModuleDto? selected;
    [ObservableProperty] private ServerModuleSelection? selectedRow;
    private IReadOnlyList<ServerModuleSelection> _rows = [];
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private ServerModulePlanDto? plan;
    [ObservableProperty] private string operationText = "";
    [ObservableProperty] private IReadOnlyList<ServerInstalledModuleDto> installedPackages = [];
    [ObservableProperty] private IReadOnlyList<ServerInstalledModuleDto> selectedVersions = [];
    [ObservableProperty] private ServerInstalledModuleDto? selectedPackageVersion;
    [ObservableProperty] private IReadOnlyList<ServerPackageOperationDto> packageOperations = [];
    [ObservableProperty] private IReadOnlyList<ServerPackageOperationRow> packageOperationRows = [];
    [ObservableProperty] private string packageStatus = "";
    [ObservableProperty] private ServerModulePackagePlanDto? packagePlan;
    [ObservableProperty] private string storeQuery = "";
    [ObservableProperty] private string storeStatus = "";
    [ObservableProperty] private IReadOnlyList<ServerModuleStoreItemDto> storeItems = [];
    [ObservableProperty] private IReadOnlyList<ServerModuleStoreSourceDto> storeSources = [];
    [ObservableProperty] private string catalogRevision = "uninitialized";
    [ObservableProperty] private ServerModuleStoreItemDto? selectedStoreItem;
    [ObservableProperty] private ServerModuleHealthPageDto? healthReport;
    [ObservableProperty] private IReadOnlyList<ServerModuleHealthRow> healthRows = [];
    private bool _storeSearched;

    public bool HasPackagePlan => PackagePlan is not null;
    public bool CanApplyPackagePlan => PackagePlan is not null && !Busy && CanManage;
    public bool CanRollbackPackage => !Busy && CanManage && SelectedPackageVersion is { Active: false };
    public bool CanInstallStoreItem => !Busy && CanManage
        && SelectedStoreItem is { Revoked: false, RidCompatible: true, HostApiCompatible: true };
    private int _versionRequest;
    public IReadOnlyList<ServerModulePolicyChoice> PolicyChoices { get; } = [new("KeepLoaded"), new("IdleDelayed"), new("ReleaseOnLogout")];
    [ObservableProperty] private ServerModulePolicyChoice policyChoice = new("IdleDelayed");
    [ObservableProperty] private decimal idleGraceSeconds = 300;
    public bool CanSetPolicy => CanPreview(ServerModuleAction.SetResidencyPolicy) && IdleGraceSeconds is >= 0 and <= 604800
        && decimal.Truncate(IdleGraceSeconds) == IdleGraceSeconds;
    public bool CanEditPolicy => CanManage
        && Selected?.AllowedActions?.Any(action => action.Action == ServerModuleAction.SetResidencyPolicy && action.Allowed) == true;
    partial void OnPolicyChoiceChanged(ServerModulePolicyChoice value) { Plan = null; Notify(); }
    partial void OnIdleGraceSecondsChanged(decimal value) { Plan = null; Notify(); }
    private Guid? _operationId;
    private bool _operationPending;
    private string? _requestedModuleId;
    private bool _suppressSelectionNotify;
    public bool IsCurrent => client.IsCurrent;

    // ── Counts and catalog ───────────────────────────────────────────────────────────────────
    public int InstalledCount => Modules.Count;
    public int EnabledCount => Modules.Count(module => module.EffectiveEnabled);
    public int LoadedCount => Modules.Count(module => module.Residency?.State is "active" or "resident" or "loading");
    public int PendingRestartCount => Modules.Count(module => module.RestartRequired);
    public bool HasModules => Modules.Count > 0;
    public LocalizedStatus StatusText => StatusMessage(Status);
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public string ServiceIdText => Modules.Select(module => module.ServiceId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? "—";
    public bool HasServiceId => ServiceIdText != "—";
    public bool IsHostRecovery => Modules.Any(module => module.RecoveryState == "host-recovery");

    public IEnumerable<ServerModuleSelection> VisibleModules => _rows.Where(Matches);
    public bool HasVisibleModules => VisibleModules.Any();
    public bool IsFilteredEmpty => _rows.Count > 0 && !VisibleModules.Any();
    private bool Matches(ServerModuleSelection row)
    {
        var fact = row.Fact;
        var query = Search.Trim();
        if (query.Length > 0
            && !fact.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            && !(fact.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            && !ModuleDisplayName(fact).Resolve().Contains(query, StringComparison.OrdinalIgnoreCase)) return false;
        return SelectedFilter.Filter switch
        {
            ServerModuleFilter.Enabled => fact.DesiredEnabled,
            ServerModuleFilter.Disabled => !fact.DesiredEnabled,
            ServerModuleFilter.PendingRestart => fact.RestartRequired,
            _ => true,
        };
    }
    public LocalizedStatus ListEmptyText => Busy && !HasModules
        ? LocalizedStatus.Key("server_modules.list.loading")
        : !HasModules ? LocalizedStatus.Key("server_modules.list.empty")
        : IsFilteredEmpty ? LocalizedStatus.Key("server_modules.list.empty_filtered")
        : LocalizedStatus.Literal("");
    public bool ShowListEmpty => !string.IsNullOrEmpty(ListEmptyText.Resolve());
    private void SyncSelectedRow()
    {
        var row = _rows.FirstOrDefault(candidate => candidate.Fact.Id == Selected?.Id && Matches(candidate));
        if (!ReferenceEquals(SelectedRow, row)) SelectedRow = row;
    }
    partial void OnSearchChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleModules));
        OnPropertyChanged(nameof(HasVisibleModules));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(ShowListEmpty));
        OnPropertyChanged(nameof(ListEmptyText));
        SyncSelectedRow();
    }

    public int SelectedCount => _rows.Count(row => row.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    [RelayCommand] private void ClearSelection()
    {
        _suppressSelectionNotify = true;
        try { foreach (var row in _rows) row.IsSelected = false; }
        finally { _suppressSelectionNotify = false; }
        Plan = null;
        Notify();
    }
    public LocalizedStatus SelectionText => LocalizedStatus.Format("server_modules.selected_count", SelectedCount);

    public void LocateModule(string? moduleId)
    {
        _requestedModuleId = moduleId;
        Search = ""; SelectedFilter = FilterItems[0];
        if (moduleId is not null) SelectedRow = _rows.FirstOrDefault(x => x.Fact.Id == moduleId);
        if (!Busy) _ = RefreshCommand.ExecuteAsync(null);
    }

    // ── Selected module facts ────────────────────────────────────────────────────────────────
    public bool HasSelectedModule => Selected is not null;
    public LocalizedStatus SelectedTitle => Selected is { } module ? ModuleDisplayName(module) : LocalizedStatus.Key("server_modules.select_module");
    public string SelectedId => Selected?.Id ?? "";
    public LocalizedStatus SelectedStatusText => Selected is { } module ? DescribeModule(module).Text : LocalizedStatus.Literal("");
    private string SelectedTone => Selected is { } module ? DescribeModule(module).Tone : "";
    public bool SelectedToneOk => SelectedTone == "ok";
    public bool SelectedToneWarn => SelectedTone == "warn";
    public bool SelectedToneBad => SelectedTone == "bad";
    public string SelectedVersionText => Selected is { } module ? VersionOf(module) : "—";
    public bool SelectedReadOnly => Selected is { } module && IsOnlyForbidden(module);
    public bool ShowSelectedReadOnly => SelectedReadOnly && !IsReadOnly;

    public string SelectedInstalledVersionText => Selected?.InstalledVersion ?? "—";
    public string SelectedDesiredVersionText => Selected?.DesiredVersion ?? "—";
    public string SelectedEffectiveVersionText => Selected?.EffectiveVersion ?? "—";
    public LocalizedStatus SelectedPackagingText => Selected is { } module ? PackagingText(module.Packaging) : LocalizedStatus.Literal("—");
    public string SelectedSourceText => Selected?.SourceId ?? "—";
    public LocalizedStatus SelectedTrustText => Selected is { } module ? TrustText(module.TrustState) : LocalizedStatus.Literal("—");
    public LocalizedStatus SelectedOwnerText => Selected is { } module
        ? module.ConfigurationWritable ? OwnerText(module.ConfigurationOwner)
            : LocalizedStatus.Format("server_modules.owner.read_only_value", OwnerText(module.ConfigurationOwner))
        : LocalizedStatus.Literal("—");
    public string SelectedGenerationText => Selected?.ProcessGeneration ?? "—";
    public string SelectedRidText => Selected?.Rid ?? "—";
    public string SelectedHostApiText => Selected?.HostApiRequirement ?? "—";
    public LocalizedStatus SelectedReleaseLevelText => Selected is { } module ? ReleaseLevelText(module.ResourceReleaseLevel) : LocalizedStatus.Literal("—");

    public IReadOnlyList<string> SelectedCapabilities => Selected?.Capabilities ?? [];
    public bool HasSelectedCapabilities => SelectedCapabilities.Count > 0;
    public IReadOnlyList<string> SelectedRequires => Selected?.Requires ?? [];
    public bool HasSelectedRequires => SelectedRequires.Count > 0;
    public IReadOnlyList<string> SelectedRequiredBy => Selected?.RequiredBy ?? [];
    public bool HasSelectedRequiredBy => SelectedRequiredBy.Count > 0;
    public IReadOnlyList<string> SelectedOrphanedWork => Selected?.OrphanedWork ?? [];
    public bool HasSelectedOrphanedWork => SelectedOrphanedWork.Count > 0;

    public bool SelectedHasResidency => Selected?.Residency is not null;
    public LocalizedStatus SelectedResidencyStateText => Selected?.Residency is { } residency
        ? ResidencyStateText(residency.State)
        : LocalizedStatus.Key(Selected?.EffectiveEnabled == true ? "server_modules.residency.unknown" : "server_modules.residency.disabled");
    public bool SelectedResidencyToneOk => Selected?.Residency?.State is "active" or "resident";
    public bool SelectedResidencyToneWarn => Selected?.Residency?.State is "loading" or "draining" or "unloading";
    public bool SelectedResidencyToneBad => Selected?.Residency?.State == "faulted";
    public bool SelectedResidencyToneIdle => Selected?.Residency is null || Selected?.Residency?.State is "dormant";
    public string SelectedLeasesText => (Selected?.Residency?.ActiveLeases ?? 0).ToString(CultureInfo.CurrentCulture);
    public LocalizedStatus SelectedReleasableText => LocalizedStatus.Key(Selected?.Residency?.Releasable == true
        ? "server_modules.residency.releasable" : "server_modules.residency.not_releasable");
    public string SelectedLastUsedText => FormatTime(Selected?.Residency?.LastUsedAt);
    public string SelectedLastReleasedText => FormatTime(Selected?.Residency?.LastReleasedAt);
    public string SelectedWakeText => Selected?.Residency?.LastWakeMilliseconds is { } milliseconds
        ? string.Format(CultureInfo.CurrentCulture, "{0:0} ms", milliseconds) : "—";
    public bool HasSelectedBlocked => !string.IsNullOrWhiteSpace(Selected?.Residency?.BlockedBy);
    public LocalizedStatus SelectedBlockedText => ExplainProblem(Selected?.Residency?.BlockedBy);
    public bool HasSelectedFailure => !string.IsNullOrWhiteSpace(Selected?.Residency?.Failure);
    public LocalizedStatus SelectedFailureText => ExplainProblem(Selected?.Residency?.Failure);

    public LocalizedStatus SelectedDesiredPolicyText => PolicyValue(Selected?.DesiredResidencyPolicy);
    public LocalizedStatus SelectedEffectivePolicyText => PolicyValue(Selected?.EffectiveResidencyPolicy);
    public LocalizedStatus PolicyHint
    {
        get
        {
            if (!CanManage) return LocalizedStatus.Key("server_modules.action.read_only");
            var admission = Selected?.AllowedActions?.FirstOrDefault(action => action.Action == ServerModuleAction.SetResidencyPolicy);
            if (admission is { Allowed: true }) return LocalizedStatus.Key("server_modules.policy_hint");
            var blocker = admission?.BlockedBy.FirstOrDefault();
            return blocker is null ? LocalizedStatus.Key("server_modules.action.admission_unknown")
                : ExplainProblem(blocker.ProblemCode, blocker.ModuleId);
        }
    }

    // ── Actions and plan ─────────────────────────────────────────────────────────────────────
    public IReadOnlyList<ServerModuleActionRow> ActionRows { get; private set; } = [];
    public LocalizedStatus ActionTargetText => _rows.Any(row => row.IsSelected)
        ? LocalizedStatus.Format("server_modules.detail.selection_target", SelectedCount)
        : Selected is { } module ? LocalizedStatus.Format("server_modules.detail.current_module", ModuleDisplayName(module))
        : LocalizedStatus.Key("server_modules.action.select_first");

    private IReadOnlyList<ServerModuleDto> Targets => _rows.Any(row => row.IsSelected)
        ? _rows.Where(row => row.IsSelected).Select(row => row.Fact).ToArray()
        : Selected is { } module ? [module] : [];

    public bool CanEnable => CanPreview(ServerModuleAction.Enable) && Targets.Any(module => !module.DesiredEnabled);
    public bool CanDisable => CanPreview(ServerModuleAction.Disable) && Targets.Any(module => module.DesiredEnabled);
    public bool CanWake => CanPreview(ServerModuleAction.Wake);
    public bool CanSleep => CanPreview(ServerModuleAction.TrySleep);
    private bool CanPreview(ServerModuleAction action) => !Busy && client.IsCurrent && Targets.Count > 0
        && Targets.All(module => module.AllowedActions?.Any(item => item.Action == action && item.Allowed) == true);

    public bool CanManage => Modules.Count > 0 && !IsReadOnly && client.IsCurrent;
    public bool IsReadOnly => Modules.Count > 0 && Modules.All(IsOnlyForbidden);

    public bool HasPlan => Plan is not null;
    public bool PlanRequiresRestart => Plan?.RestartRequired == true;
    public LocalizedStatus PlanActionText => Plan is { } plan ? ActionText(plan.Target.Action) : LocalizedStatus.Literal("");
    public LocalizedStatus PlanTargetText => Plan is { } plan ? ModuleNames(plan.Target.ModuleIds) : LocalizedStatus.Literal("");
    public LocalizedStatus PlanAddedText => Plan is { } plan ? ModuleNames(plan.Added) : LocalizedStatus.Literal("");
    public LocalizedStatus PlanRemovedText => Plan is { } plan ? ModuleNames(plan.Removed) : LocalizedStatus.Literal("");
    public LocalizedStatus PlanRestartText => LocalizedStatus.Key(Plan?.RestartRequired == true
        ? "server_modules.plan.restart_yes" : "server_modules.plan.restart_no");
    public LocalizedStatus PlanExpiresText => Plan is { } plan
        ? LocalizedStatus.Format("server_modules.plan.expires", FormatTime(plan.ExpiresAt)) : LocalizedStatus.Literal("");
    public bool HasPlanBlockers => Plan is { BlockedBy.Count: > 0 };
    public LocalizedStatus PlanBlockedText => Plan is { BlockedBy.Count: > 0 } plan
        ? LocalizedStatus.Join(" · ", plan.BlockedBy.Select(blocker => ExplainProblem(blocker.ProblemCode, blocker.ModuleId)))
        : LocalizedStatus.Literal("");
    public string PlanServiceText => Plan?.ServiceId ?? "";
    public bool CanApply => !Busy && client.IsCurrent && references is not null && connectionId is not null
        && Plan is { BlockedBy.Count: 0 } plan && plan.ExpiresAt > DateTimeOffset.UtcNow;
    public bool HasOperation => !string.IsNullOrWhiteSpace(OperationText);

    [RelayCommand] private void DiscardPlan() => Plan = null;

    private void RebuildActionRows()
    {
        var targets = Targets;
        ActionRows =
        [
            new(ServerModuleAction.Enable, ActionText(ServerModuleAction.Enable), ActionHint(ServerModuleAction.Enable, CanEnable, targets), CanEnable, EnableCommand),
            new(ServerModuleAction.Disable, ActionText(ServerModuleAction.Disable), ActionHint(ServerModuleAction.Disable, CanDisable, targets), CanDisable, DisableCommand),
            new(ServerModuleAction.Wake, ActionText(ServerModuleAction.Wake), ActionHint(ServerModuleAction.Wake, CanWake, targets), CanWake, WakeCommand),
            new(ServerModuleAction.TrySleep, ActionText(ServerModuleAction.TrySleep), ActionHint(ServerModuleAction.TrySleep, CanSleep, targets), CanSleep, SleepCommand),
        ];
        OnPropertyChanged(nameof(ActionRows));
    }
    private LocalizedStatus ActionHint(ServerModuleAction action, bool allowed, IReadOnlyList<ServerModuleDto> targets)
    {
        if (Busy) return LocalizedStatus.Key("server_modules.action.busy");
        if (!client.IsCurrent) return LocalizedStatus.Key("server_modules.action.session_changed");
        if (targets.Count == 0) return LocalizedStatus.Key("server_modules.action.select_first");
        // A page-wide read-only boundary is stated once above the tabs. Repeating it in four
        // action rows would turn the explanation back into the wall of identical lines this page
        // is meant to replace; the disabled commands and the notice carry the fact.
        if (IsReadOnly) return LocalizedStatus.Literal("");
        if (action == ServerModuleAction.Enable && targets.All(module => module.DesiredEnabled))
            return LocalizedStatus.Key("server_modules.action.already_enabled");
        if (action == ServerModuleAction.Disable && targets.All(module => !module.DesiredEnabled))
            return LocalizedStatus.Key("server_modules.action.already_disabled");
        if (action == ServerModuleAction.Wake && targets.All(module => module.Residency?.State is "active" or "resident"))
            return LocalizedStatus.Key("server_modules.action.already_loaded");
        var admissions = targets.Select(module => module.AllowedActions?.FirstOrDefault(item => item.Action == action)).ToArray();
        if (admissions.Any(admission => admission is null))
            return LocalizedStatus.Key("server_modules.action.admission_unknown");
        var blocked = admissions.FirstOrDefault(admission => admission is { Allowed: false });
        if (blocked is not null)
        {
            var blocker = blocked.BlockedBy.FirstOrDefault();
            return blocker is null ? LocalizedStatus.Key("server_modules.action.unavailable")
                : ExplainProblem(blocker.ProblemCode, blocker.ModuleId);
        }
        return allowed ? LocalizedStatus.Key("server_modules.action." + ActionKey(action) + "_hint")
            : LocalizedStatus.Key("server_modules.action.unavailable");
    }
    private static string ActionKey(ServerModuleAction action) => action switch
    {
        ServerModuleAction.Enable => "enable",
        ServerModuleAction.Disable => "disable",
        ServerModuleAction.Wake => "wake",
        ServerModuleAction.TrySleep => "sleep",
        ServerModuleAction.SetResidencyPolicy => "policy",
        _ => action.ToString().ToLowerInvariant(),
    };
    private static LocalizedStatus ActionText(ServerModuleAction action) =>
        LocalizedStatus.Key("server_modules.action." + ActionKey(action), action.ToString());
    private LocalizedStatus ModuleNames(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return LocalizedStatus.Key("server_modules.field.none");
        return LocalizedStatus.Join(", ", ids.Select(id => Modules.FirstOrDefault(module => module.Id == id) is { } module
            ? ModuleDisplayName(module) : LocalizedStatus.Literal(id)));
    }

    public void Notify()
    {
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(PendingRestartCount));
        OnPropertyChanged(nameof(HasModules));
        OnPropertyChanged(nameof(ServiceIdText));
        OnPropertyChanged(nameof(HasServiceId));
        OnPropertyChanged(nameof(IsHostRecovery));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsReadOnly));
        if (IsReadOnly && SelectedTab?.Tab != ServerModulesTab.Modules) SelectedTab = TabItems[0];
        else if (SelectedTab is null) SelectedTab = TabItems[0];
        OnPropertyChanged(nameof(VisibleTabs));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(HasSelectedModule));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedId));
        OnPropertyChanged(nameof(SelectedStatusText));
        OnPropertyChanged(nameof(SelectedToneOk));
        OnPropertyChanged(nameof(SelectedToneWarn));
        OnPropertyChanged(nameof(SelectedToneBad));
        OnPropertyChanged(nameof(SelectedVersionText));
        OnPropertyChanged(nameof(SelectedReadOnly));
        OnPropertyChanged(nameof(ShowSelectedReadOnly));
        OnPropertyChanged(nameof(SelectedInstalledVersionText));
        OnPropertyChanged(nameof(SelectedDesiredVersionText));
        OnPropertyChanged(nameof(SelectedEffectiveVersionText));
        OnPropertyChanged(nameof(SelectedPackagingText));
        OnPropertyChanged(nameof(SelectedSourceText));
        OnPropertyChanged(nameof(SelectedTrustText));
        OnPropertyChanged(nameof(SelectedOwnerText));
        OnPropertyChanged(nameof(SelectedGenerationText));
        OnPropertyChanged(nameof(SelectedRidText));
        OnPropertyChanged(nameof(SelectedHostApiText));
        OnPropertyChanged(nameof(SelectedReleaseLevelText));
        OnPropertyChanged(nameof(SelectedCapabilities));
        OnPropertyChanged(nameof(HasSelectedCapabilities));
        OnPropertyChanged(nameof(SelectedRequires));
        OnPropertyChanged(nameof(HasSelectedRequires));
        OnPropertyChanged(nameof(SelectedRequiredBy));
        OnPropertyChanged(nameof(HasSelectedRequiredBy));
        OnPropertyChanged(nameof(SelectedOrphanedWork));
        OnPropertyChanged(nameof(HasSelectedOrphanedWork));
        OnPropertyChanged(nameof(SelectedHasResidency));
        OnPropertyChanged(nameof(SelectedResidencyStateText));
        OnPropertyChanged(nameof(SelectedResidencyToneOk));
        OnPropertyChanged(nameof(SelectedResidencyToneWarn));
        OnPropertyChanged(nameof(SelectedResidencyToneBad));
        OnPropertyChanged(nameof(SelectedResidencyToneIdle));
        OnPropertyChanged(nameof(SelectedLeasesText));
        OnPropertyChanged(nameof(SelectedReleasableText));
        OnPropertyChanged(nameof(SelectedLastUsedText));
        OnPropertyChanged(nameof(SelectedLastReleasedText));
        OnPropertyChanged(nameof(SelectedWakeText));
        OnPropertyChanged(nameof(HasSelectedBlocked));
        OnPropertyChanged(nameof(SelectedBlockedText));
        OnPropertyChanged(nameof(HasSelectedFailure));
        OnPropertyChanged(nameof(SelectedFailureText));
        OnPropertyChanged(nameof(SelectedDesiredPolicyText));
        OnPropertyChanged(nameof(SelectedEffectivePolicyText));
        OnPropertyChanged(nameof(PolicyHint));
        OnPropertyChanged(nameof(ActionTargetText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(PlanRequiresRestart));
        OnPropertyChanged(nameof(PlanActionText));
        OnPropertyChanged(nameof(PlanTargetText));
        OnPropertyChanged(nameof(PlanAddedText));
        OnPropertyChanged(nameof(PlanRemovedText));
        OnPropertyChanged(nameof(PlanRestartText));
        OnPropertyChanged(nameof(PlanExpiresText));
        OnPropertyChanged(nameof(HasPlanBlockers));
        OnPropertyChanged(nameof(PlanBlockedText));
        OnPropertyChanged(nameof(PlanServiceText));
        OnPropertyChanged(nameof(HasOperation));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(CanDisable));
        OnPropertyChanged(nameof(CanWake));
        OnPropertyChanged(nameof(CanSleep));
        OnPropertyChanged(nameof(CanSetPolicy));
        OnPropertyChanged(nameof(CanEditPolicy));
        OnPropertyChanged(nameof(CanApplyPackagePlan));
        OnPropertyChanged(nameof(CanRollbackPackage));
        OnPropertyChanged(nameof(CanInstallStoreItem));
        OnPropertyChanged(nameof(CanRestoreAll));
        OnPropertyChanged(nameof(CanLoadPrivilege));
        OnPropertyChanged(nameof(CanApprovePrivilege));
        OnPropertyChanged(nameof(CanCancelPrivilege));
        OnPropertyChanged(nameof(CanPrepareConsent));
        OnPropertyChanged(nameof(CanApproveConsent));
        OnPropertyChanged(nameof(CanRevokeConsent));
        RebuildActionRows();
        EnableCommand.NotifyCanExecuteChanged(); DisableCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged();
        WakeCommand.NotifyCanExecuteChanged(); SleepCommand.NotifyCanExecuteChanged();
        SetPolicyCommand.NotifyCanExecuteChanged(); ApplyPackagePlanCommand.NotifyCanExecuteChanged();
        RollbackPackageCommand.NotifyCanExecuteChanged(); InstallStoreItemCommand.NotifyCanExecuteChanged();
        RestoreAllCommand.NotifyCanExecuteChanged();
        LoadPrivilegeCommand.NotifyCanExecuteChanged();
        ApprovePrivilegeCommand.NotifyCanExecuteChanged();
        CancelPrivilegeCommand.NotifyCanExecuteChanged();
        PrepareConsentCommand.NotifyCanExecuteChanged();
        ApproveConsentCommand.NotifyCanExecuteChanged();
        RevokeConsentCommand.NotifyCanExecuteChanged();
    }

    public void Invalidate()
    {
        InvalidateConsents();
        PrivilegePlan = null; PrivilegePassword = ""; PrivilegeAdministrator = ""; PrivilegeText = ""; PrivilegePlanId = "";
        Modules = []; Selected = null; Plan = null; OperationText = ""; InstalledPackages = []; SelectedVersions = [];
        SelectedPackageVersion = null; PackageOperations = []; PackageOperationRows = []; PackagePlan = null;
        StoreItems = []; StoreSources = []; CatalogRevision = "uninitialized"; SelectedStoreItem = null; StoreStatus = "";
        HealthReport = null; HealthRows = []; PackageStatus = "module-session-changed";
        Status = "module-session-changed";
        Notify();
    }

    [RelayCommand] private async Task RefreshAsync() => await Run(async () =>
    {
        // Clear before reading: failed refreshes show unknown instead of stale successful facts.
        var selectedId = Selected?.Id;
        Modules = []; Selected = null; Plan = null;
        Modules = await client.ReadAsync();
        Selected = Modules.FirstOrDefault(x => x.Id == (_requestedModuleId ?? selectedId))
            ?? (_requestedModuleId is null ? Modules.FirstOrDefault() : null);
        if (_requestedModuleId is not null && Selected is null) Status = "module-not-found";
        if (Selected is not null) _requestedModuleId = null;
        SelectedRow = _rows.FirstOrDefault(x => x.Fact.Id == Selected?.Id);
        if (Modules.Count > 0 && Modules.All(module => module.AllowedActions?.All(action => action.BlockedBy.Any(reason => reason.ProblemCode == "module-forbidden")) == true))
            OperationText = "";
        else
        {
            var history = await client.HistoryAsync();
            OperationText = string.Join("\n", history.Items.Select(x =>
                $"{x.OperationId} · {LocalizedText.Get("server_modules.operation.state." + x.State)}"
                + (x.ProblemCode is null ? "" : " · " + ExplainProblem(x.ProblemCode).Resolve())));
        }
        if (references is not null && connectionId is not null && Modules.Count > 0)
            foreach (var original in references.Read(connectionId, actor))
            {
                if (original.ServiceId != Modules[0].ServiceId) throw new InvalidOperationException("module-server-mismatch");
                var result = await client.PlanStatusAsync(original.PlanId);
                if (System.Text.Json.JsonSerializer.Serialize(result.Plan.Target, RelaxKonOSJsonOptions.Default)
                    != System.Text.Json.JsonSerializer.Serialize(original.Target, RelaxKonOSJsonOptions.Default))
                    throw new InvalidOperationException("module-response-invalid");
                if (result.Operation is { } recovered)
                {
                    OperationText += "\n" + DescribeOperation(recovered);
                    if (Pending(recovered)) { _operationId = recovered.OperationId; _operationPending = true; }
                    if (recovered.State == ServerModuleOperationState.Failed || recovered.State == ServerModuleOperationState.Succeeded && MatchesTarget(original, recovered))
                        references.Resolve(connectionId, actor, original.PlanId);
                    else if (recovered.State == ServerModuleOperationState.Succeeded) Status = "module-effective-target-mismatch";
                }
                else OperationText += $"\n{original.PlanId} · {LocalizedText.Get("server_modules.unsubmitted")}";
            }
        if (_operationId is { } id)
        {
            var operation = await client.OperationAsync(id);
            OperationText = $"{id} · {LocalizedText.Get("server_modules.operation.state." + operation.State)}" + "\n" + OperationText;
        }
        await RefreshPackageFactsAsync();
        Notify();
    });

    private async Task RefreshPackageFactsAsync()
    {
        try
        {
            var installed = await client.InstalledPackagesAsync();
            var operations = await client.PackageOperationsAsync();
            InstalledPackages = installed.Items;
            PackageOperations = operations.Items;
            PackageStatus = "";
            await RefreshSelectedVersionsAsync();
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException
            or System.Text.Json.JsonException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            InstalledPackages = []; SelectedVersions = []; PackageOperations = []; PackageOperationRows = [];
            PackageStatus = "server_modules.packages_unavailable";
        }
    }

    private async Task RefreshSelectedVersionsAsync()
    {
        var moduleId = Selected?.Id;
        var request = ++_versionRequest;
        if (moduleId is null) { SelectedVersions = []; return; }
        try
        {
            var versions = await client.PackageVersionsAsync(moduleId);
            if (request == _versionRequest && Selected?.Id == moduleId) SelectedVersions = versions.Items;
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException
            or System.Text.Json.JsonException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            if (request == _versionRequest) SelectedVersions = [];
        }
    }

    public async Task InstallPackageAsync(Stream content, long length) => await Run(async () =>
    {
        PackageStatus = LocalizedText.Get("server_modules.package_uploading");
        var upload = await client.UploadPackageAsync(content, length);
        PackagePlan = await client.AutoPlanPackageAsync(upload.UploadId);
        PackageStatus = LocalizedText.Get("server_modules.package_plan_ready");
    });

    [RelayCommand(CanExecute = nameof(CanApplyPackagePlan))]
    private async Task ApplyPackagePlanAsync() => await Run(async () =>
    {
        if (PackagePlan is not { } plan) return;
        var result = await client.ApplyPackagePlanAsync(plan.PlanId);
        PackagePlan = null;
        await RefreshPackageFactsAsync();
        OperationText = LocalizedText.Get("server_modules.package_action.install")
            + $" · {result.OperationId} · {result.Record.ModuleId} {result.Record.Version}";
        PackageStatus = LocalizedText.Get("server_modules.package_installed_restart");
    });

    [RelayCommand(CanExecute = nameof(CanRollbackPackage))]
    private async Task RollbackPackageAsync() => await Run(async () =>
    {
        if (SelectedPackageVersion is not { Active: false } target) return;
        var result = await client.RollbackPackageAsync(target.ModuleId, target.Version);
        await RefreshPackageFactsAsync();
        await RefreshSelectedVersionsAsync();
        OperationText = LocalizedText.Get("server_modules.package_action.rollback")
            + $" · {result.OperationId} · {result.Record.ModuleId} {result.Record.Version}";
        PackageStatus = LocalizedText.Get("server_modules.package_rolled_back");
    });

    [RelayCommand] private async Task SearchStoreAsync() => await Run(async () =>
    {
        var sources = await client.StoreSourcesAsync();
        StoreSources = sources.Sources;
        CatalogRevision = sources.CatalogRevision;
        StoreItems = (await client.SearchStoreAsync(StoreQuery)).Items;
        SelectedStoreItem = StoreItems.FirstOrDefault();
        _storeSearched = true;
        OnPropertyChanged(nameof(ShowStoreEmpty));
        OnPropertyChanged(nameof(ShowStoreSourcesEmpty));
        StoreStatus = StoreItems.Count == 0 ? LocalizedText.Get("server_modules.store_empty") : "";
    });

    [RelayCommand] private async Task RefreshCatalogsAsync() => await Run(async () =>
    {
        var page = await client.StoreSourcesAsync();
        var revision = page.CatalogRevision;
        var refreshed = 0;
        foreach (var source in page.Sources.Where(source => source.CatalogUriConfigured))
        {
            try
            {
                var receipt = await client.RefreshCatalogAsync(source.SourceId, revision,
                    "ui-refresh-" + Guid.NewGuid().ToString("N"));
                revision = receipt.Revision;
                refreshed++;
            }
            catch (InvalidOperationException) { }
        }
        var current = await client.StoreSourcesAsync();
        StoreSources = current.Sources;
        CatalogRevision = current.CatalogRevision;
        StoreItems = (await client.SearchStoreAsync(StoreQuery)).Items;
        SelectedStoreItem = StoreItems.FirstOrDefault();
        _storeSearched = true;
        OnPropertyChanged(nameof(ShowStoreEmpty));
        OnPropertyChanged(nameof(ShowStoreSourcesEmpty));
        StoreStatus = refreshed == 0
            ? LocalizedText.Get("server_modules.store_refresh_none")
            : LocalizedText.Get("server_modules.store_refreshed");
    });

    [RelayCommand(CanExecute = nameof(CanInstallStoreItem))]
    private async Task InstallStoreItemAsync() => await Run(async () =>
    {
        if (SelectedStoreItem is not { } item) return;
        StoreStatus = LocalizedText.Get("server_modules.store_downloading");
        var upload = await client.DownloadStorePackageAsync(item.SourceId, item.ReleaseId);
        PackagePlan = await client.AutoPlanPackageAsync(upload.UploadId);
        PackageStatus = LocalizedText.Get("server_modules.package_plan_ready");
    });

    [RelayCommand] private async Task CheckHealthAsync() => await Run(async () =>
    {
        var report = await client.CheckHealthAsync();
        HealthReport = report;
    });

    [RelayCommand(CanExecute = nameof(CanEnable))] private Task EnableAsync() => PreviewAsync(true);
    [RelayCommand(CanExecute = nameof(CanDisable))] private Task DisableAsync() => PreviewAsync(false);
    [RelayCommand(CanExecute = nameof(CanWake))] private Task WakeAsync() => PreviewRuntimeAsync(ServerModuleAction.Wake);
    [RelayCommand(CanExecute = nameof(CanSleep))] private Task SleepAsync() => PreviewRuntimeAsync(ServerModuleAction.TrySleep);
    [RelayCommand(CanExecute = nameof(CanSetPolicy))] private Task SetPolicyAsync() => Run(async () =>
    {
        var targets = Targets;
        if (targets.FirstOrDefault() is not { ServiceId: { } service, DesiredRevision: { } revision }) return;
        Plan = await client.PreviewAsync(new(service, ServerModuleAction.SetResidencyPolicy,
            targets.Select(x => x.Id).Order(StringComparer.Ordinal).ToArray(),
            Modules.Where(x => x.DesiredEnabled).Select(x => x.Id).Order(StringComparer.Ordinal).ToArray(), revision, Guid.NewGuid().ToString("N"),
            Strategy: PolicyChoice.Value, IdleGraceSeconds: decimal.ToInt32(IdleGraceSeconds)));
    });
    private Task PreviewRuntimeAsync(ServerModuleAction action) => Run(async () =>
    {
        var targets = Targets;
        if (targets.FirstOrDefault() is not { ServiceId: { } service, DesiredRevision: { } revision }) return;
        Plan = await client.PreviewAsync(new(service, action, targets.Select(x => x.Id).Order(StringComparer.Ordinal).ToArray(),
            Modules.Where(x => x.DesiredEnabled).Select(x => x.Id).Order(StringComparer.Ordinal).ToArray(), revision, Guid.NewGuid().ToString("N")));
    });
    [RelayCommand(CanExecute = nameof(CanRestoreAll))] private Task RestoreAllAsync() => Run(async () =>
    {
        var first = Modules[0];
        var ids = Modules.Select(module => module.Id).Order(StringComparer.Ordinal).ToArray();
        Plan = await client.PreviewAsync(new(first.ServiceId!, ServerModuleAction.Enable, ids, ids, first.DesiredRevision!, Guid.NewGuid().ToString("N")));
    });
    private async Task PreviewAsync(bool enabled) => await Run(async () =>
    {
        // The target set is the checked rows when any exist, so a batch change must not depend on
        // whichever row happens to be open in the detail pane.
        var targets = Targets;
        if (targets.FirstOrDefault() is not { ServiceId: { } service, DesiredRevision: { } revision }) return;
        var desired = Modules.Where(x => x.DesiredEnabled).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var module in targets) { if (enabled) desired.Add(module.Id); else desired.Remove(module.Id); }
        Plan = await client.PreviewAsync(new(service, enabled ? ServerModuleAction.Enable : ServerModuleAction.Disable,
            targets.Select(x => x.Id).Order(StringComparer.Ordinal).ToArray(), desired.Order(StringComparer.Ordinal).ToArray(), revision, Guid.NewGuid().ToString("N")));
    });
    [RelayCommand(CanExecute = nameof(CanApply))] private async Task ApplyAsync() => await Run(async () =>
    {
        if (Plan is not { } plan) return;
        if (references is null || connectionId is null) throw new InvalidOperationException("module-reference-unavailable");
        references.Save(connectionId, actor, plan);
        // Keep the plan on transport failure: repeating Apply recovers its original receipt.
        OperationText = LocalizedText.Get("server_modules.waiting");
        var operation = await client.ApplyAsync(plan.PlanId);
        if (!operation.EnabledModuleIds.ToHashSet(StringComparer.Ordinal).SetEquals(plan.Target.EnabledModuleIds))
            throw new InvalidOperationException("module-response-invalid");
        _operationId = operation.OperationId;
        _operationPending = Pending(operation);
        OperationText = DescribeOperation(operation);
        Plan = null;
        var selectedId = Selected?.Id;
        Modules = await client.ReadAsync(); Selected = Modules.FirstOrDefault(x => x.Id == selectedId);
        Notify();
    });
    private async Task Run(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; Status = "";
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException or IOException or UnauthorizedAccessException)
        { Status = ex is InvalidOperationException ? ex.Message : "module-state-unavailable"; if (!client.IsCurrent) Invalidate(); }
        finally { Busy = false; }
    }

    // ── Package/version/health/store projections ────────────────────────────────────────────
    public bool HasInstalledPackages => InstalledPackages.Count > 0;
    public bool HasSelectedVersions => SelectedVersions.Count > 0;
    public bool HasSelectedVersion => SelectedPackageVersion is not null;
    public LocalizedStatus SelectedVersionStateText => LocalizedStatus.Key(SelectedPackageVersion?.Active == true
        ? "server_modules.package.active" : "server_modules.package.installed");
    public string SelectedVersionReleaseText => SelectedPackageVersion?.ReleaseId ?? "—";
    public string SelectedVersionDigestText => ShortDigest(SelectedPackageVersion?.ArtifactSha256);
    public string SelectedVersionInstalledAtText => FormatTime(SelectedPackageVersion?.InstalledAt);
    public bool HasPackageOperations => PackageOperationRows.Count > 0;
    public LocalizedStatus PackageStatusText => StatusMessage(PackageStatus);
    public bool HasPackageStatus => !string.IsNullOrWhiteSpace(PackageStatus);
    public bool HasPackagePlanPrivileges => PackagePlan is { Privileges.Count: > 0 };
    public string PackagePlanTitle => PackagePlan?.Name ?? "";
    public string PackagePlanSummary => PackagePlan?.Summary ?? "";
    public string PackagePlanModuleText => PackagePlan?.ModuleId ?? "—";
    public string PackagePlanVersionText => PackagePlan?.Version ?? "—";
    public string PackagePlanPublisherText => PackagePlan?.PublisherId ?? "—";
    public string PackagePlanRuntimeText => PackagePlan?.Runtime ?? "—";
    public string PackagePlanReleaseText => PackagePlan?.ReleaseId ?? "—";
    public string PackagePlanDigestText => ShortDigest(PackagePlan?.ArtifactSha256);
    public string PackagePlanExpiresText => FormatTime(PackagePlan?.ExpiresAt);
    public string PackagePlanPrivilegesText => PackagePlan is { } plan && plan.Privileges.Count > 0
        ? string.Join("\n", plan.Privileges.Select(privilege =>
            $"{privilege.Kind} · {string.Join(',', privilege.Platforms)}"
            + (privilege.Executable ? "" : " · unsupported")
            + $"\n  {string.Join("\n  ", privilege.Resources)}")) : "—";

    public IReadOnlyList<ServerModuleStoreSourceRow> StoreSourceRows => StoreSources.Select(source => new ServerModuleStoreSourceRow(source)).ToArray();
    public bool HasStoreSources => StoreSources.Count > 0;
    public bool ShowStoreSourcesEmpty => _storeSearched && StoreSources.Count == 0;
    public bool HasCatalogRevision => CatalogRevision is not ("" or "uninitialized");
    public bool HasStoreItems => StoreItems.Count > 0;
    public bool ShowStoreEmpty => _storeSearched && StoreItems.Count == 0;
    public LocalizedStatus StoreStatusText => StatusMessage(StoreStatus);
    public bool HasStoreStatus => !string.IsNullOrWhiteSpace(StoreStatus);
    public bool HasSelectedStoreItem => SelectedStoreItem is not null;
    public string SelectedStoreModuleText => SelectedStoreItem?.ModuleId ?? "—";
    public string SelectedStoreVersionText => SelectedStoreItem?.Version ?? "—";
    public string SelectedStoreSourceText => SelectedStoreItem?.SourceId ?? "—";
    public string SelectedStorePublisherText => SelectedStoreItem?.PublisherId ?? "—";
    public LocalizedStatus SelectedStoreTrustText => SelectedStoreItem is { } item ? TrustText(item.TrustState) : LocalizedStatus.Literal("—");
    public LocalizedStatus SelectedStoreCompatibilityText => LocalizedStatus.Key(SelectedStoreItem is { RidCompatible: true, HostApiCompatible: true }
        ? "server_modules.store.compatibility_ok" : "server_modules.store.compatibility_bad");
    public string SelectedStoreRidText => SelectedStoreItem is { } item ? $"{item.Rid} · API {item.HostApiMinimum}..<{item.HostApiMaximumExclusive}" : "—";
    public string SelectedStoreArchiveText => SelectedStoreItem is { } item ? string.Format(CultureInfo.CurrentCulture, "{0:N0} B", item.ArchiveBytes) : "—";
    public string SelectedStoreDigestText => ShortDigest(SelectedStoreItem?.ArtifactSha256);
    public string SelectedStoreDependenciesText => SelectedStoreItem is { } item && item.Dependencies.Count > 0
        ? string.Join(" · ", item.Dependencies.Select(dependency => $"{dependency.ModuleId} {dependency.MinimumVersion}..<{dependency.MaximumVersionExclusive}"))
        : "—";
    public string SelectedStoreCapabilitiesText => SelectedStoreItem is { } item && item.Capabilities.Count > 0
        ? string.Join(" · ", item.Capabilities) : "—";
    public bool HasSelectedStoreBlockers => SelectedStoreItem is { BlockedBy.Count: > 0 };
    public LocalizedStatus SelectedStoreBlockedText => SelectedStoreItem is { BlockedBy.Count: > 0 } item
        ? LocalizedStatus.Join(" · ", item.BlockedBy.Select(blocker => ExplainProblem(blocker.ProblemCode, blocker.ModuleId)))
        : LocalizedStatus.Literal("");

    public bool HasHealthReport => HealthReport is not null;
    public LocalizedStatus HealthSummaryText => HealthReport is { } report
        ? LocalizedStatus.Key(report.Healthy ? "server_modules.health.healthy" : "server_modules.health.unhealthy")
        : LocalizedStatus.Key("server_modules.health.not_checked");
    public LocalizedStatus HealthCheckedText => HealthReport is { } report
        ? LocalizedStatus.Format("server_modules.health.checked_at", FormatTime(report.CheckedAt)) : LocalizedStatus.Literal("");
    public bool HealthToneOk => HealthReport?.Healthy == true;
    public bool HealthToneBad => HealthReport?.Healthy == false;

    // ── Consent projections ─────────────────────────────────────────────────────────────────
    public LocalizedStatus ConsentModuleText => Selected is { } module
        ? LocalizedStatus.Format("server_modules.consent.module_value", ModuleDisplayName(module), module.Id)
        : LocalizedStatus.Key("server_modules.consent.none_selected");
    public bool CanPrepareConsent => !Busy && CanManage && Selected is not null;
    public bool CanApproveConsent => !Busy && CanManage && ConsentPlan is { State: "AwaitingConfirmation" } plan
        && plan.ExpiresAt > DateTimeOffset.UtcNow;
    public bool CanRevokeConsent => !Busy && CanManage && SelectedConsent is not null;

    // ── Lifecycle ───────────────────────────────────────────────────────────────────────────
    public bool ShouldObserveOperation => _operationPending && _operationId is not null && client.IsCurrent && !Busy;
    private static bool Pending(ServerModuleOperationDto operation) => operation.State is ServerModuleOperationState.Prepared
        or ServerModuleOperationState.Applying or ServerModuleOperationState.Verifying or ServerModuleOperationState.AwaitingRestart;
    private static string DescribeOperation(ServerModuleOperationDto operation) => $"{operation.OperationId} · "
        + LocalizedText.Get("server_modules.operation.state." + operation.State)
        + (operation.ProblemCode is null ? "" : " · " + ExplainProblem(operation.ProblemCode).Resolve()) + "\n"
        + string.Join("\n", operation.ModuleResults?.Select(result =>
            $"{result.ModuleId} · {LocalizedText.Get(result.Succeeded ? "server_modules.operation.member_ok" : "server_modules.operation.member_failed")}"
            + (result.ProblemCode is null ? "" : " · " + ExplainProblem(result.ProblemCode).Resolve())) ?? []);
    private bool MatchesTarget(ServerModulePlanDto original, ServerModuleOperationDto operation)
    {
        if (original.Target.Action is ServerModuleAction.Wake or ServerModuleAction.TrySleep) return true;
        if (!operation.EnabledModuleIds.ToHashSet(StringComparer.Ordinal).SetEquals(Modules.Where(x => x.EffectiveEnabled).Select(x => x.Id))) return false;
        return original.Target.Action != ServerModuleAction.SetResidencyPolicy || original.Target.ModuleIds.All(id =>
            Modules.SingleOrDefault(x => x.Id == id)?.EffectiveResidencyPolicy == new ServerModuleResidencyPolicyDto(original.Target.Strategy!, original.Target.IdleGraceSeconds!.Value));
    }
    public Task ObserveOperationAsync() => !ShouldObserveOperation ? Task.CompletedTask : Run(async () =>
    {
        var operation = await client.OperationAsync(_operationId!.Value);
        OperationText = DescribeOperation(operation);
        _operationPending = Pending(operation);
        if (!_operationPending && Plan is null)
        {
            var selectedId = Selected?.Id;
            Modules = await client.ReadAsync();
            Selected = Modules.FirstOrDefault(x => x.Id == selectedId);
        }
        Notify();
    });
    public bool CanRestoreAll => IsHostRecovery && !Busy && client.IsCurrent
        && Modules.All(module => module.AllowedActions?.Any(action => action.Action == ServerModuleAction.Enable && action.Allowed) == true);

    // ── Property change wiring ──────────────────────────────────────────────────────────────
    partial void OnSelectedChanged(ServerModuleDto? value)
    {
        ClearConsentReview();
        Plan = null;
        if (value?.DesiredResidencyPolicy is { } policy)
        {
            PolicyChoice = PolicyChoices.FirstOrDefault(choice => choice.Value == policy.Strategy) ?? PolicyChoices[1];
            IdleGraceSeconds = policy.IdleGraceSeconds;
        }
        var row = _rows.FirstOrDefault(x => x.Fact.Id == value?.Id);
        if (!ReferenceEquals(SelectedRow, row)) SelectedRow = row;
        Notify();
        _ = RefreshSelectedVersionsAsync();
    }
    partial void OnSelectedRowChanged(ServerModuleSelection? value)
    {
        // Filtering may remove the selected row from the list; keep the detail pane on the module
        // the user was reading instead of turning the search box into a context reset.
        if (value is not null && !ReferenceEquals(Selected, value.Fact)) Selected = value.Fact;
    }
    partial void OnModulesChanged(IReadOnlyList<ServerModuleDto> value)
    {
        _rows = value.Select(module => new ServerModuleSelection(module, () =>
        {
            if (_suppressSelectionNotify) return;
            Plan = null; Notify();
        })).ToArray();
        var keep = Selected?.Id;
        var next = keep is null ? null : value.FirstOrDefault(module => module.Id == keep);
        if (!ReferenceEquals(next, Selected)) Selected = next;
        SelectedRow = _rows.FirstOrDefault(x => x.Fact.Id == Selected?.Id);
        OnPropertyChanged(nameof(VisibleModules)); OnPropertyChanged(nameof(HasVisibleModules));
        OnPropertyChanged(nameof(IsFilteredEmpty)); OnPropertyChanged(nameof(ShowListEmpty));
        OnPropertyChanged(nameof(ListEmptyText));
        Notify();
    }
    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnPlanChanged(ServerModulePlanDto? value) { Notify(); }
    partial void OnOperationTextChanged(string value) => OnPropertyChanged(nameof(HasOperation));
    partial void OnInstalledPackagesChanged(IReadOnlyList<ServerInstalledModuleDto> value)
    {
        OnPropertyChanged(nameof(HasInstalledPackages)); Notify();
    }
    partial void OnSelectedVersionsChanged(IReadOnlyList<ServerInstalledModuleDto> value)
    {
        OnPropertyChanged(nameof(HasSelectedVersions));
        if (SelectedPackageVersion is null || !value.Any(module => module.ModuleId == SelectedPackageVersion.ModuleId
                && module.Version == SelectedPackageVersion.Version))
            SelectedPackageVersion = value.FirstOrDefault(module => module.Active) ?? value.FirstOrDefault();
        Notify();
    }
    partial void OnSelectedPackageVersionChanged(ServerInstalledModuleDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedVersion));
        OnPropertyChanged(nameof(SelectedVersionStateText));
        OnPropertyChanged(nameof(SelectedVersionReleaseText));
        OnPropertyChanged(nameof(SelectedVersionDigestText));
        OnPropertyChanged(nameof(SelectedVersionInstalledAtText));
        Notify();
    }
    partial void OnPackageOperationsChanged(IReadOnlyList<ServerPackageOperationDto> value)
    {
        PackageOperationRows = value.Select(operation => new ServerPackageOperationRow(operation)).ToArray();
        OnPropertyChanged(nameof(HasPackageOperations));
    }
    partial void OnPackagePlanChanged(ServerModulePackagePlanDto? value)
    {
        OnPropertyChanged(nameof(HasPackagePlan));
        OnPropertyChanged(nameof(PackagePlanTitle));
        OnPropertyChanged(nameof(PackagePlanSummary));
        OnPropertyChanged(nameof(PackagePlanModuleText));
        OnPropertyChanged(nameof(PackagePlanVersionText));
        OnPropertyChanged(nameof(PackagePlanPublisherText));
        OnPropertyChanged(nameof(PackagePlanRuntimeText));
        OnPropertyChanged(nameof(PackagePlanReleaseText));
        OnPropertyChanged(nameof(PackagePlanDigestText));
        OnPropertyChanged(nameof(PackagePlanExpiresText));
        OnPropertyChanged(nameof(PackagePlanPrivilegesText));
        OnPropertyChanged(nameof(HasPackagePlanPrivileges));
        Notify();
    }
    partial void OnSelectedStoreItemChanged(ServerModuleStoreItemDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedStoreItem));
        OnPropertyChanged(nameof(SelectedStoreModuleText));
        OnPropertyChanged(nameof(SelectedStoreVersionText));
        OnPropertyChanged(nameof(SelectedStoreSourceText));
        OnPropertyChanged(nameof(SelectedStorePublisherText));
        OnPropertyChanged(nameof(SelectedStoreTrustText));
        OnPropertyChanged(nameof(SelectedStoreCompatibilityText));
        OnPropertyChanged(nameof(SelectedStoreRidText));
        OnPropertyChanged(nameof(SelectedStoreArchiveText));
        OnPropertyChanged(nameof(SelectedStoreDigestText));
        OnPropertyChanged(nameof(SelectedStoreDependenciesText));
        OnPropertyChanged(nameof(SelectedStoreCapabilitiesText));
        OnPropertyChanged(nameof(HasSelectedStoreBlockers));
        OnPropertyChanged(nameof(SelectedStoreBlockedText));
        Notify();
    }
    partial void OnStoreSourcesChanged(IReadOnlyList<ServerModuleStoreSourceDto> value)
    {
        OnPropertyChanged(nameof(StoreSourceRows));
        OnPropertyChanged(nameof(HasStoreSources));
        OnPropertyChanged(nameof(ShowStoreSourcesEmpty));
    }
    partial void OnStoreItemsChanged(IReadOnlyList<ServerModuleStoreItemDto> value)
    {
        OnPropertyChanged(nameof(HasStoreItems));
        OnPropertyChanged(nameof(ShowStoreEmpty));
    }
    partial void OnCatalogRevisionChanged(string value) => OnPropertyChanged(nameof(HasCatalogRevision));
    partial void OnHealthReportChanged(ServerModuleHealthPageDto? value)
    {
        HealthRows = value?.Modules.Select(module => new ServerModuleHealthRow(module)).ToArray() ?? [];
        OnPropertyChanged(nameof(HasHealthReport));
        OnPropertyChanged(nameof(HealthSummaryText));
        OnPropertyChanged(nameof(HealthCheckedText));
        OnPropertyChanged(nameof(HealthToneOk));
        OnPropertyChanged(nameof(HealthToneBad));
    }
    partial void OnPackageStatusChanged(string value)
    {
        OnPropertyChanged(nameof(PackageStatusText));
        OnPropertyChanged(nameof(HasPackageStatus));
    }
    partial void OnStoreStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StoreStatusText));
        OnPropertyChanged(nameof(HasStoreStatus));
    }

    // ── Formatting helpers (shared with row records) ────────────────────────────────────────
    internal static LocalizedStatus ModuleDisplayName(ServerModuleDto module) => module.Id switch
    {
        "relaxkonos.terminal" => LocalizedStatus.Key("server_modules.module.terminal"),
        "relaxkonos.firewall" => LocalizedStatus.Key("server_modules.module.firewall"),
        "relaxkonos.system-performance" => LocalizedStatus.Key("server_modules.module.system_performance"),
        "relaxkonos.git" => LocalizedStatus.Key("server_modules.module.git"),
        "relaxkonos.tailscale" => LocalizedStatus.Key("server_modules.module.tailscale"),
        "relaxkonos.docker" => LocalizedStatus.Key("server_modules.module.docker"),
        "relaxkonos.certificates" => LocalizedStatus.Key("server_modules.module.certificates"),
        "relaxkonos.webserver" => LocalizedStatus.Key("server_modules.module.web_server"),
        "relaxkonos.proxy" => LocalizedStatus.Key("server_modules.module.proxy"),
        _ => string.IsNullOrWhiteSpace(module.Name) ? LocalizedStatus.Literal(module.Id) : LocalizedStatus.Literal(module.Name),
    };
    internal static string VersionOf(ServerModuleDto module) =>
        module.InstalledVersion ?? module.EffectiveVersion ?? module.DesiredVersion ?? "—";
    internal static (LocalizedStatus Text, string Tone) DescribeModule(ServerModuleDto module)
    {
        if (module.RecoveryState == "host-recovery") return (LocalizedStatus.Key("server_modules.state.recovery"), "warn");
        if (!module.DesiredEnabled && !module.EffectiveEnabled) return (LocalizedStatus.Key("server_modules.state.disabled"), "idle");
        if (module.DesiredEnabled && !module.EffectiveEnabled) return (LocalizedStatus.Key("server_modules.state.pending_enable"), "warn");
        if (!module.DesiredEnabled && module.EffectiveEnabled) return (LocalizedStatus.Key("server_modules.state.pending_disable"), "warn");
        if (module.RestartRequired) return (LocalizedStatus.Key("server_modules.state.pending_restart"), "warn");
        return module.Residency?.State switch
        {
            "active" => (LocalizedStatus.Key("server_modules.state.loaded"), "ok"),
            "resident" => (LocalizedStatus.Key("server_modules.state.resident"), "ok"),
            "loading" => (LocalizedStatus.Key("server_modules.state.loading"), "warn"),
            "draining" => (LocalizedStatus.Key("server_modules.state.draining"), "warn"),
            "unloading" => (LocalizedStatus.Key("server_modules.state.unloading"), "warn"),
            "dormant" => (LocalizedStatus.Key("server_modules.state.dormant"), "idle"),
            "faulted" => (LocalizedStatus.Key("server_modules.state.faulted"), "bad"),
            null => (LocalizedStatus.Key("server_modules.state.unknown"), "idle"),
            _ => (LocalizedStatus.Literal(module.Residency!.State), "idle"),
        };
    }
    internal static LocalizedStatus ResidencyStateText(string state) => state switch
    {
        "active" => LocalizedStatus.Key("server_modules.state.loaded"),
        "resident" => LocalizedStatus.Key("server_modules.state.resident"),
        "loading" => LocalizedStatus.Key("server_modules.state.loading"),
        "draining" => LocalizedStatus.Key("server_modules.state.draining"),
        "unloading" => LocalizedStatus.Key("server_modules.state.unloading"),
        "dormant" => LocalizedStatus.Key("server_modules.state.dormant"),
        "faulted" => LocalizedStatus.Key("server_modules.state.faulted"),
        _ => LocalizedStatus.Literal(state),
    };
    internal static LocalizedStatus StatusLabel(string? status) => string.IsNullOrWhiteSpace(status)
        ? LocalizedStatus.Literal("—")
        : LocalizedStatus.Key("server_modules.status." + status, status);
    internal static LocalizedStatus StoreSourceStatus(string? status) => string.IsNullOrWhiteSpace(status)
        ? LocalizedStatus.Literal("—")
        : LocalizedStatus.Key("server_modules.store_source." + status, status);
    internal static LocalizedStatus PackageActionText(string action) => action switch
    {
        "install" => LocalizedStatus.Key("server_modules.package_action.install"),
        "rollback" => LocalizedStatus.Key("server_modules.package_action.rollback"),
        _ => LocalizedStatus.Literal(action),
    };
    internal static LocalizedStatus PackagingText(string value) => value switch
    {
        "bundled" => LocalizedStatus.Key("server_modules.packaging.bundled"),
        "independent" => LocalizedStatus.Key("server_modules.packaging.independent"),
        _ => LocalizedStatus.Literal(value),
    };
    internal static LocalizedStatus OwnerText(string value) => value switch
    {
        "managed" => LocalizedStatus.Key("server_modules.owner.managed"),
        "deployment" => LocalizedStatus.Key("server_modules.owner.deployment"),
        _ => LocalizedStatus.Literal(value),
    };
    internal static LocalizedStatus TrustText(string value) => value switch
    {
        "host" => LocalizedStatus.Key("server_modules.trust.host"),
        "developer" => LocalizedStatus.Key("server_modules.trust.developer"),
        _ => LocalizedStatus.Literal(value),
    };
    internal static LocalizedStatus ReleaseLevelText(string value) => value switch
    {
        "objects" => LocalizedStatus.Key("server_modules.release.objects"),
        "none" => LocalizedStatus.Key("server_modules.release.none"),
        _ => LocalizedStatus.Literal(value),
    };
    private static LocalizedStatus PolicyValue(ServerModuleResidencyPolicyDto? policy) => policy is null
        ? LocalizedStatus.Literal("—")
        : LocalizedStatus.Format("server_modules.residency.policy_value",
            LocalizedStatus.Key("server_modules.strategy." + policy.Strategy, policy.Strategy), policy.IdleGraceSeconds);
    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
    private static string ShortDigest(string? digest) =>
        string.IsNullOrWhiteSpace(digest) ? "—" : digest.Length <= 16 ? digest : digest[..16];
    private static LocalizedStatus StatusMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return LocalizedStatus.Literal("");
        return value.StartsWith("server_modules.", StringComparison.Ordinal)
            ? LocalizedStatus.Key(value) : ExplainProblem(value);
    }
    private static bool IsOnlyForbidden(ServerModuleDto module) =>
        module.AllowedActions is { Count: > 0 } actions
        && actions.All(action => !action.Allowed && action.BlockedBy.Count > 0
            && action.BlockedBy.All(blocker => blocker.ProblemCode == "module-forbidden"));

    internal static LocalizedStatus ExplainProblem(string? code, string? moduleId = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return LocalizedStatus.Literal("");
        var text = code switch
        {
            "module-forbidden" => LocalizedStatus.Key("server_modules.problem.module_forbidden"),
            "module-configuration-read-only" => LocalizedStatus.Key("server_modules.problem.module_configuration_read_only"),
            "module-recovery-mode" => LocalizedStatus.Key("server_modules.problem.module_recovery_mode"),
            "module-recovery-blocked" => LocalizedStatus.Key("server_modules.problem.module_recovery_blocked"),
            "module-active-work" => LocalizedStatus.Key("server_modules.problem.module_active_work"),
            "module-policy-keeps-loaded" => LocalizedStatus.Key("server_modules.problem.module_policy_keeps_loaded"),
            "module-resident-work-owner" => LocalizedStatus.Key("server_modules.problem.module_resident_work_owner"),
            "module-resident-dependent" => LocalizedStatus.Key("server_modules.problem.module_resident_dependent"),
            "module-disabled" => LocalizedStatus.Key("server_modules.problem.module_disabled"),
            "module-pending-disable" => LocalizedStatus.Key("server_modules.problem.module_pending_disable"),
            "module-dependency-blocked" => LocalizedStatus.Key("server_modules.problem.module_dependency_blocked"),
            "module-action-unsupported" => LocalizedStatus.Key("server_modules.problem.module_action_unsupported"),
            "module-not-found" => LocalizedStatus.Key("server_modules.problem.module_not_found"),
            "module-state-unavailable" => LocalizedStatus.Key("server_modules.problem.module_state_unavailable"),
            "module-revision-conflict" => LocalizedStatus.Key("server_modules.problem.module_revision_conflict"),
            "module-plan-expired" => LocalizedStatus.Key("server_modules.problem.module_plan_expired"),
            "module-server-mismatch" => LocalizedStatus.Key("server_modules.problem.module_server_mismatch"),
            "module-effective-target-mismatch" => LocalizedStatus.Key("server_modules.problem.module_effective_target_mismatch"),
            "module-response-invalid" => LocalizedStatus.Key("server_modules.problem.module_response_invalid"),
            "module-reference-unavailable" => LocalizedStatus.Key("server_modules.problem.module_reference_unavailable"),
            "module-operation-interrupted" => LocalizedStatus.Key("server_modules.problem.module_operation_interrupted"),
            "module-idempotency-conflict" => LocalizedStatus.Key("server_modules.problem.module_idempotency_conflict"),
            "module.privilege_plan_expired" => LocalizedStatus.Key("server_modules.problem.module_privilege_plan_expired"),
            "module.privilege_plan_not_found" => LocalizedStatus.Key("server_modules.problem.module_privilege_plan_not_found"),
            "module.privilege_consent_invalid" => LocalizedStatus.Key("server_modules.problem.module_privilege_consent_invalid"),
            "module-session-changed" => LocalizedStatus.Key("server_modules.problem.module_session_changed"),
            "module-package-quarantined" => LocalizedStatus.Key("server_modules.problem.module_package_quarantined"),
            "module-package-load-failed" => LocalizedStatus.Key("server_modules.problem.module_package_load_failed"),
            "module-package-source-not-accepted" => LocalizedStatus.Key("server_modules.problem.module_package_source_not_accepted"),
            "module-package-release-revoked" => LocalizedStatus.Key("server_modules.problem.module_package_release_revoked"),
            "module-package-rid-mismatch" => LocalizedStatus.Key("server_modules.problem.module_package_rid_mismatch"),
            "module-package-host-api-incompatible" => LocalizedStatus.Key("server_modules.problem.module_package_host_api_incompatible"),
            "module-package-artifact-verification-required" => LocalizedStatus.Key("server_modules.problem.module_package_artifact_verification_required"),
            "module-package-dependency-verification-required" => LocalizedStatus.Key("server_modules.problem.module_package_dependency_verification_required"),
            "module-package-actions-unsupported" => LocalizedStatus.Key("server_modules.problem.module_package_actions_unsupported"),
            "module-packaging-bundled" => LocalizedStatus.Key("server_modules.problem.module_packaging_bundled"),
            "module-package-installer-api-unavailable" => LocalizedStatus.Key("server_modules.problem.module_package_installer_api_unavailable"),
            "module-wake-overloaded" => LocalizedStatus.Key("server_modules.problem.module_wake_overloaded"),
            "module-load-failed" => LocalizedStatus.Key("server_modules.problem.module_load_failed"),
            "module-wake-timeout" => LocalizedStatus.Key("server_modules.problem.module_wake_timeout"),
            "module-host-stopping" => LocalizedStatus.Key("server_modules.problem.module_host_stopping"),
            "module-release-blocked" => LocalizedStatus.Key("server_modules.problem.module_release_blocked"),
            "module-stream-cleanup-failed" => LocalizedStatus.Key("server_modules.problem.module_stream_cleanup_failed"),
            "module-background-work-failed" => LocalizedStatus.Key("server_modules.problem.module_background_work_failed"),
            _ => LocalizedStatus.Literal(code),
        };
        return moduleId is null ? text : LocalizedStatus.Format("server_modules.problem.with_module", text, moduleId);
    }
}
