using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record ComponentInspectionStep(TaskStep Step)
{
    public LocalizedStatus Effects => Step.RequiredCapabilities.IsEmpty ? LocalizedStatus.Literal("")
        : LocalizedStatus.Key(TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(Step.Inputs).Impact.DeletesBusinessData
            ? "component_center.change_deletes_data" : "component_center.change_preserves_data");
    public LocalizedStatus Title => LocalizedStatus.Key(Step.ActionId == "runtime.verifyInstallation"
        ? "component_center.inspect_installation" : Step.ActionId == "runtime.verifyCatalog" ? "component_center.inspect_catalog"
            : Step.ActionId == "runtime.uninstall" ? "proxy_server.runtime.uninstall_title"
            : "proxy_server.runtime.action." + Enum.Parse<RuntimeChange>(Step.ActionId["runtime.".Length..], true));
    public LocalizedStatus State => LocalizedStatus.Key("component_center.inspection." + Step.Disposition);
    public LocalizedStatus Problems => LocalizedStatus.Join("\n", Step.Problems.Select(code => code.StartsWith("runtime.", StringComparison.Ordinal)
        ? ProxyServerRuntimeViewModel.ProblemText(code) : LocalizedStatus.Key("component_center.inspection_changed")));
}

public partial class ComponentCenterViewModel
{
    private readonly ITaskPlanPreviewClient? _previews;
    private readonly CancellationTokenSource _inspectionLifetime = new();
    private RuntimeInstallationSnapshot? _inspectionInstallation;
    private RuntimeCatalogSnapshot? _inspectionCatalog;
    private int _inspectionGeneration;
    [ObservableProperty] private bool isInspecting;
    [ObservableProperty] private LocalizedStatus inspectionNotice = LocalizedStatus.Literal("");
    public ObservableCollection<ComponentInspectionStep> InspectionSteps { get; } = [];
    public bool ShowInspection => _previews is not null && HasRuntimeDetails && SelectedPage is not ComponentCenterPage.Tasks;
    public bool CanInspect => _previews is not null && !IsTaskBusy && !IsInspecting && !Runtime.IsBusy && Runtime.CanRead
        && !Runtime.IsInvalidated && Runtime.Installation is not null;
    partial void OnIsInspectingChanged(bool value)
    { OnPropertyChanged(nameof(CanInspect)); OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(CanPlanDraft)); TaskProperties(); }

    private void ObserveInspectionState()
    {
        if (ReferenceEquals(_inspectionInstallation, Runtime.Installation)
            && ReferenceEquals(_inspectionCatalog, Runtime.CatalogSnapshot) && !Runtime.IsInvalidated) return;
        _inspectionInstallation = Runtime.Installation;
        _inspectionCatalog = Runtime.CatalogSnapshot;
        _inspectionGeneration++;
        ClearChangePlan();
        InspectionSteps.Clear();
        InspectionNotice = LocalizedStatus.Literal("");
    }

    [RelayCommand] private async Task InspectAsync()
    {
        Runtime.SynchronizeAccess();
        if (!CanInspect || Runtime.Installation is not { } installation) return;
        var generation = _inspectionGeneration;
        var steps = new List<TaskStepIntent>
        {
            new("installation", "runtime.verifyInstallation", 1, "sing-box-read-1",
                JsonSerializer.SerializeToElement(new VerifyRuntimeInstallationInput(installation.Installation.InstallationId,
                    installation.Installation.StateRevision)), [])
        };
        if (Runtime.CatalogSnapshot is { Catalog: { } catalog } snapshot)
            steps.Add(new("catalog", "runtime.verifyCatalog", 1, "sing-box-read-1",
                JsonSerializer.SerializeToElement(new VerifyRuntimeCatalogInput(snapshot.Source.Identity.SourceId,
                    catalog.Revision, snapshot.Source.Identity.TrustRevision)), ["installation"]));
        var intent = new TaskPlanIntent("app", "relaxkonos.component-center", 1, [.. steps]);
        IsInspecting = true;
        InspectionSteps.Clear();
        InspectionNotice = LocalizedStatus.Key("component_center.inspection_running");
        try
        {
            var plan = await _previews!.PreviewAsync(intent, installation.Installation.HostId, _inspectionLifetime.Token);
            Runtime.SynchronizeAccess();
            if (generation != _inspectionGeneration || Runtime.IsInvalidated || !Runtime.CanRead || _inspectionLifetime.IsCancellationRequested) return;
            foreach (var step in plan.Semantics.Steps) InspectionSteps.Add(new(step));
            InspectionNotice = LocalizedStatus.Key(steps.Count > 1
                ? "component_center.inspection_complete" : "component_center.inspection_no_catalog");
        }
        catch (OperationCanceledException) when (_inspectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException)
        {
            if (generation == _inspectionGeneration && !_inspectionLifetime.IsCancellationRequested)
                InspectionNotice = LocalizedStatus.Key(ex.Message switch
                {
                    "task.permission_denied" => "component_center.inspection_denied",
                    "task.plan_capacity_exceeded" => "component_center.inspection_capacity",
                    "task.plan_store_unavailable" => "component_center.inspection_storage_failed",
                    _ => "component_center.inspection_failed"
                });
        }
        finally { IsInspecting = false; }
    }
}
