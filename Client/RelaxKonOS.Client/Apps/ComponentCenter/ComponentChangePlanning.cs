using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public partial class ComponentCenterViewModel
{
    private long _draftRevision;
    [ObservableProperty] private TaskPlan? frozenChangePlan;
    [ObservableProperty] private string changePlanDigest = "";
    [ObservableProperty] private LocalizedStatus changePlanNotice = LocalizedStatus.Literal("");
    public bool CanPlanDraft => _previews is not null && CanPreview;
    public System.Collections.ObjectModel.ObservableCollection<ComponentInspectionStep> ChangePlanSteps { get; } = [];

    private void ClearChangePlan()
    {
        ChangePlanDigest = "";
        FrozenChangePlan = null;
        ChangePlanSteps.Clear();
        ChangePlanNotice = LocalizedStatus.Literal("");
        OnPropertyChanged(nameof(CanPlanDraft));
    }

    [RelayCommand] private async Task PlanDraftAsync()
    {
        Runtime.SynchronizeAccess();
        if (!CanPlanDraft || Runtime.Installation is not { } installation) return;
        ClearChangePlan();
        var generation = _inspectionGeneration;
        var revision = _draftRevision;
        var steps = new List<TaskStepIntent>();
        foreach (var item in Drafts)
        {
            var draft = item.Draft;
            var catalog = Runtime.CatalogSnapshot;
            if (draft.RuntimeId != "sing-box" || draft.ComponentId != "sing-box" || draft.HostId != installation.Installation.HostId
                || draft.InstallationId != installation.Installation.InstallationId
                || draft.Change != RuntimeChange.Uninstall && (catalog?.Catalog is null || catalog.Source.Identity.SourceId != draft.SourceId
                    || !catalog.Catalog.Releases.Any(release => release.ReleaseId == draft.ReleaseId && release.OriginalVersion == draft.Version)))
            { ChangePlanNotice = LocalizedStatus.Key("component_center.draft_stale"); return; }
            var input = new RuntimeTaskChangeInput(draft.InstallationId, draft.HostId, installation.Installation.StateRevision,
                draft.Change == RuntimeChange.Uninstall ? 0 : catalog!.Catalog!.Revision,
                draft.Change == RuntimeChange.Uninstall ? 0 : catalog!.Source.Identity.TrustRevision,
                draft.SourceId, draft.ReleaseId, draft.AllowPrerelease, draft.KeepData);
            steps.Add(new("change-" + draft.ComponentId, "runtime." + draft.Change.ToString().ToLowerInvariant(), 1,
                "sing-box-change-1", TaskPlanJson.FreezeInputs(input), []));
        }
        IsInspecting = true;
        ChangePlanNotice = LocalizedStatus.Key("component_center.inspection_running");
        try
        {
            var plan = await _previews!.PlanChangeAsync(new("app", "relaxkonos.component-center", revision, [.. steps]),
                installation.Installation.HostId, _inspectionLifetime.Token);
            Runtime.SynchronizeAccess();
            if (generation != _inspectionGeneration || revision != _draftRevision || Runtime.IsInvalidated
                || !Runtime.CanRead || !Runtime.CanManage || _inspectionLifetime.IsCancellationRequested) return;
            ChangePlanDigest = plan.PlanDigest;
            FrozenChangePlan = plan;
            foreach (var step in plan.Semantics.Steps) ChangePlanSteps.Add(new(step));
            ChangePlanNotice = LocalizedStatus.Key("component_center.change_plan_ready");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException)
        {
            if (generation == _inspectionGeneration && revision == _draftRevision && !_inspectionLifetime.IsCancellationRequested)
                ChangePlanNotice = LocalizedStatus.Key("component_center.inspection_failed");
        }
        finally { IsInspecting = false; }
    }
}
