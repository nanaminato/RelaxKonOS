using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed partial class ServerModuleSelection(ServerModuleDto fact, Action changed) : ObservableObject
{
    public ServerModuleDto Fact { get; } = fact;
    [ObservableProperty] private bool isSelected;
    partial void OnIsSelectedChanged(bool value) => changed();
}
public sealed record ServerModulePolicyChoice(string Value)
{
    public LocalizedStatus Label => LocalizedStatus.Key("server_modules.strategy." + Value);
}

public sealed partial class ServerModulesViewModel(ServerModuleManagementClient client,
    ServerModuleReferenceStore? references = null, string? connectionId = null, Guid actor = default) : ObservableObject
{
    [ObservableProperty] private IReadOnlyList<ServerModuleDto> modules = [];
    [ObservableProperty] private ServerModuleDto? selected;
    [ObservableProperty] private ServerModuleSelection? selectedRow;
    private IReadOnlyList<ServerModuleSelection> _rows = [];
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool onlyPending;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private ServerModulePlanDto? plan;
    [ObservableProperty] private string operationText = "";
    [ObservableProperty] private IReadOnlyList<ServerInstalledModuleDto> installedPackages = [];
    [ObservableProperty] private IReadOnlyList<ServerInstalledModuleDto> selectedVersions = [];
    [ObservableProperty] private ServerInstalledModuleDto? selectedPackageVersion;
    [ObservableProperty] private IReadOnlyList<ServerPackageOperationDto> packageOperations = [];
    [ObservableProperty] private string packageStatus = "";
    [ObservableProperty] private ServerModulePackagePlanDto? packagePlan;
    public bool HasPackagePlan => PackagePlan is not null;
    public bool CanApplyPackagePlan => PackagePlan is not null && !Busy;
    public bool CanRollbackPackage => !Busy && client.IsCurrent && SelectedPackageVersion is { Active: false };
    private int _versionRequest;
    public IReadOnlyList<ServerModulePolicyChoice> PolicyChoices { get; } = [new("KeepLoaded"), new("IdleDelayed"), new("ReleaseOnLogout")];
    [ObservableProperty] private ServerModulePolicyChoice policyChoice = new("IdleDelayed");
    [ObservableProperty] private decimal idleGraceSeconds = 300;
    public bool CanSetPolicy => CanPreview(ServerModuleAction.SetResidencyPolicy) && IdleGraceSeconds is >= 0 and <= 604800
        && decimal.Truncate(IdleGraceSeconds) == IdleGraceSeconds;
    partial void OnPolicyChoiceChanged(ServerModulePolicyChoice value) { Plan = null; Notify(); }
    partial void OnIdleGraceSecondsChanged(decimal value) { Plan = null; Notify(); }
    private Guid? _operationId;
    private string? _requestedModuleId;
    public bool IsCurrent => client.IsCurrent;
    public void LocateModule(string? moduleId)
    {
        _requestedModuleId = moduleId;
        Search = ""; OnlyPending = false;
        if (moduleId is not null) SelectedRow = _rows.FirstOrDefault(x => x.Fact.Id == moduleId);
        if (!Busy) _ = RefreshCommand.ExecuteAsync(null);
    }
    private bool _operationPending;
    public bool ShouldObserveOperation => _operationPending && _operationId is not null && client.IsCurrent && !Busy;
    private static bool Pending(ServerModuleOperationDto operation) => operation.State is ServerModuleOperationState.Prepared
        or ServerModuleOperationState.Applying or ServerModuleOperationState.Verifying or ServerModuleOperationState.AwaitingRestart;
    private static string DescribeOperation(ServerModuleOperationDto operation) => $"{operation.OperationId} · {operation.State} · {operation.ProblemCode}\n"
        + string.Join("\n", operation.ModuleResults?.Select(result => $"{result.ModuleId} · {result.Succeeded} · {result.ProblemCode}") ?? []);
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
    });
    public IEnumerable<ServerModuleSelection> VisibleModules => _rows.Where(x => x.Fact.Id.Contains(Search, StringComparison.OrdinalIgnoreCase)
        && (!OnlyPending || x.Fact.RestartRequired));
    private IReadOnlyList<ServerModuleDto> Targets => _rows.Any(x => x.IsSelected)
        ? _rows.Where(x => x.IsSelected).Select(x => x.Fact).ToArray() : Selected is { } module ? [module] : [];
    public string Summary => Modules.Count == 0 ? "—" : $"{Modules[0].ServiceId} · {Modules.Count(x => x.EffectiveEnabled)}/{Modules.Count} · {Modules.Count(x => x.Residency?.State is "active" or "resident")}";
    public bool IsHostRecovery => Modules.Any(module => module.RecoveryState == "host-recovery");
    public bool CanRestoreAll => IsHostRecovery && !Busy && client.IsCurrent
        && Modules.All(module => module.AllowedActions?.Any(action => action.Action == ServerModuleAction.Enable && action.Allowed) == true);
    public string Details => Selected is not { } module ? "" :
        $"{module.Id}\n{module.Packaging} · {module.InstalledVersion}\n"
        + string.Format(LocalizedText.Get("server_modules.states"), module.DesiredEnabled, module.EffectiveEnabled, module.RestartRequired) + "\n"
        + $"{module.ConfigurationOwner} · {module.ProcessGeneration}\n"
        + string.Format(LocalizedText.Get("server_modules.dependencies"), string.Join(", ", module.Requires), string.Join(", ", module.RequiredBy)) + "\n"
        + $"{module.Residency?.State} · {module.Residency?.Strategy} · leases={module.Residency?.ActiveLeases}\n"
        + $"{module.DesiredResidencyPolicy?.Strategy} / {module.DesiredResidencyPolicy?.IdleGraceSeconds}s → {module.EffectiveResidencyPolicy?.Strategy} / {module.EffectiveResidencyPolicy?.IdleGraceSeconds}s\n"
        + string.Format(LocalizedText.Get("server_modules.timing"), module.Residency?.LastUsedAt, module.Residency?.LastReleasedAt, module.Residency?.LastWakeMilliseconds) + "\n"
        + string.Join("\n", module.OrphanedWork.Concat(new[] { module.Residency?.BlockedBy, module.Residency?.Failure }.OfType<string>()).Concat(
            module.AllowedActions?.Where(x => !x.Allowed).SelectMany(x => x.BlockedBy.Select(reason => $"{x.Action}: {reason.ProblemCode}")) ?? []));
    public string PreviewText => Plan is not { } p ? "" :
        $"{p.ServiceId}\n{p.Target.Action}: {string.Join(", ", p.Target.ModuleIds)}\n+ {string.Join(", ", p.Added)}\n− {string.Join(", ", p.Removed)}\n"
        + $"{LocalizedText.Get("server_modules.restart")} {p.RestartRequired}\n{p.ExpiresAt}\n"
        + (p.Target.Action == ServerModuleAction.SetResidencyPolicy ? $"{p.Target.Strategy} · {p.Target.IdleGraceSeconds}s\n" : "")
        + string.Join("\n", p.BlockedBy.Select(x => x.ProblemCode + " " + x.ModuleId));
    public string PackageDetails => InstalledPackages.Count == 0 ? "" :
        string.Join("\n", InstalledPackages.Select(module => $"{module.ModuleId} · {module.Version} · {(module.Active ? "active" : "installed")}"
            + (module.Quarantined ? " · quarantined" : "") + $" · {module.ReleaseId}"));
    public string PackageVersionsText => SelectedVersions.Count == 0 ? "" :
        string.Join("\n", SelectedVersions.Select(module => $"{module.Version} · {(module.Active ? "active" : "installed")}"
            + (module.Quarantined ? " · quarantined" : "") + $" · {module.ArtifactSha256[..12]}"));
    public string PackageOperationText => PackageOperations.Count == 0 ? "" :
        string.Join("\n", PackageOperations.Take(20).Select(operation =>
            $"{operation.Action} · {operation.ModuleId} {operation.Version} · {operation.State} · {operation.ProblemCode}"));
    public string PackagePlanText => PackagePlan is not { } plan ? "" :
        $"{plan.ModuleId} · {plan.Version} · {plan.PublisherId}\n{plan.ReleaseId}\n{plan.ArtifactSha256}\n{plan.ExpiresAt}";
    public bool CanEnable => CanPreview(ServerModuleAction.Enable) && Targets.Any(x => !x.DesiredEnabled);
    public bool CanDisable => CanPreview(ServerModuleAction.Disable) && Targets.Any(x => x.DesiredEnabled);
    public bool CanWake => CanPreview(ServerModuleAction.Wake);
    public bool CanSleep => CanPreview(ServerModuleAction.TrySleep);
    private bool CanPreview(ServerModuleAction action) => !Busy && client.IsCurrent && Targets.Count > 0
        && Targets.All(module => module.AllowedActions?.Any(x => x.Action == action && x.Allowed) == true);
    public bool CanApply => !Busy && client.IsCurrent && references is not null && connectionId is not null
        && Plan is { BlockedBy.Count: 0 } p && p.ExpiresAt > DateTimeOffset.UtcNow;
    partial void OnSelectedChanged(ServerModuleDto? value)
    {
        Plan = null;
        if (value?.DesiredResidencyPolicy is { } policy)
        {
            PolicyChoice = PolicyChoices.Single(x => x.Value == policy.Strategy);
            IdleGraceSeconds = policy.IdleGraceSeconds;
        }
        Notify();
        _ = RefreshSelectedVersionsAsync();
    }
    partial void OnSelectedRowChanged(ServerModuleSelection? value) => Selected = value?.Fact;
    partial void OnModulesChanged(IReadOnlyList<ServerModuleDto> value)
    {
        _rows = value.Select(module => new ServerModuleSelection(module, () => { Plan = null; Notify(); })).ToArray();
        OnPropertyChanged(nameof(VisibleModules)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(IsHostRecovery));
        OnPropertyChanged(nameof(CanRestoreAll)); RestoreAllCommand.NotifyCanExecuteChanged();
    }
    partial void OnSearchChanged(string value) => OnPropertyChanged(nameof(VisibleModules));
    partial void OnOnlyPendingChanged(bool value) => OnPropertyChanged(nameof(VisibleModules));
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnPlanChanged(ServerModulePlanDto? value) { OnPropertyChanged(nameof(PreviewText)); Notify(); }
    partial void OnInstalledPackagesChanged(IReadOnlyList<ServerInstalledModuleDto> value) =>
        OnPropertyChanged(nameof(PackageDetails));
    partial void OnSelectedVersionsChanged(IReadOnlyList<ServerInstalledModuleDto> value)
    {
        OnPropertyChanged(nameof(PackageVersionsText));
        if (SelectedPackageVersion is null || !value.Any(module => module.ModuleId == SelectedPackageVersion.ModuleId
                && module.Version == SelectedPackageVersion.Version))
            SelectedPackageVersion = value.FirstOrDefault(module => module.Active) ?? value.FirstOrDefault();
        OnPropertyChanged(nameof(CanRollbackPackage)); RollbackPackageCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedPackageVersionChanged(ServerInstalledModuleDto? value)
    {
        OnPropertyChanged(nameof(CanRollbackPackage)); RollbackPackageCommand.NotifyCanExecuteChanged(); Notify();
    }
    partial void OnPackageOperationsChanged(IReadOnlyList<ServerPackageOperationDto> value) =>
        OnPropertyChanged(nameof(PackageOperationText));
    partial void OnPackagePlanChanged(ServerModulePackagePlanDto? value)
    {
        OnPropertyChanged(nameof(PackagePlanText)); OnPropertyChanged(nameof(HasPackagePlan));
        OnPropertyChanged(nameof(CanApplyPackagePlan)); ApplyPackagePlanCommand.NotifyCanExecuteChanged(); Notify();
    }
    private void Notify()
    {
        OnPropertyChanged(nameof(Details)); OnPropertyChanged(nameof(CanEnable)); OnPropertyChanged(nameof(CanDisable)); OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanWake)); OnPropertyChanged(nameof(CanSleep));
        OnPropertyChanged(nameof(CanSetPolicy));
        OnPropertyChanged(nameof(PackageDetails)); OnPropertyChanged(nameof(PackageVersionsText)); OnPropertyChanged(nameof(PackageOperationText));
        EnableCommand.NotifyCanExecuteChanged(); DisableCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged();
        WakeCommand.NotifyCanExecuteChanged(); SleepCommand.NotifyCanExecuteChanged();
        SetPolicyCommand.NotifyCanExecuteChanged(); ApplyPackagePlanCommand.NotifyCanExecuteChanged();
        RollbackPackageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRestoreAll)); RestoreAllCommand.NotifyCanExecuteChanged();
    }
    public void Invalidate() { Modules = []; Selected = null; Plan = null; OperationText = ""; InstalledPackages = []; SelectedVersions = []; SelectedPackageVersion = null; PackageOperations = []; PackagePlan = null; PackageStatus = "module-session-changed"; Notify(); }
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
        if (Modules.All(module => module.AllowedActions?.All(action => action.BlockedBy.Any(reason => reason.ProblemCode == "module-forbidden")) == true))
        { OperationText = ""; return; }
        var history = await client.HistoryAsync();
        OperationText = string.Join("\n", history.Items.Select(x => $"{x.OperationId} · {x.State} · {x.ProblemCode}"));
        if (references is not null && connectionId is not null)
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
            OperationText = $"{id} · {operation.State} · {operation.ProblemCode}\n" + OperationText;
        }
        await RefreshPackageFactsAsync();
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
            InstalledPackages = []; SelectedVersions = []; PackageOperations = [];
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
        OperationText = $"package install · {result.OperationId} · {result.Record.ModuleId} {result.Record.Version}";
        PackageStatus = LocalizedText.Get("server_modules.package_installed_restart");
    });

    [RelayCommand(CanExecute = nameof(CanRollbackPackage))]
    private async Task RollbackPackageAsync() => await Run(async () =>
    {
        if (SelectedPackageVersion is not { Active: false } target) return;
        var result = await client.RollbackPackageAsync(target.ModuleId, target.Version);
        await RefreshPackageFactsAsync();
        await RefreshSelectedVersionsAsync();
        OperationText = $"package rollback · {result.OperationId} · {result.Record.ModuleId} {result.Record.Version}";
        PackageStatus = LocalizedText.Get("server_modules.package_rolled_back");
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
        if (Selected is not { } selected || selected.ServiceId is null || selected.DesiredRevision is null) return;
        var desired = Modules.Where(x => x.DesiredEnabled).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var targets = Targets;
        foreach (var module in targets) { if (enabled) desired.Add(module.Id); else desired.Remove(module.Id); }
        Plan = await client.PreviewAsync(new(selected.ServiceId, enabled ? ServerModuleAction.Enable : ServerModuleAction.Disable,
            targets.Select(x => x.Id).Order(StringComparer.Ordinal).ToArray(), desired.Order(StringComparer.Ordinal).ToArray(), selected.DesiredRevision, Guid.NewGuid().ToString("N")));
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
}
