using System.Windows.Input;

namespace RelaxKonOS.AppSDK;

public interface IServerModuleCatalogViewModel
{
    bool IsModulesTab { get; }
    int InstalledCount { get; }
    string Search { get; set; }
    IReadOnlyList<IServerModuleFilterChoice> Filters { get; }
    IServerModuleFilterChoice SelectedFilter { get; set; }
    IEnumerable<IServerModuleSelection> VisibleModules { get; }
    IServerModuleSelection? SelectedRow { get; set; }
    bool ShowListEmpty { get; }
    string ListEmptyText { get; }
    bool HasSelection { get; }
    string SelectionText { get; }
    ICommand ClearSelectionCommand { get; }
    bool HasPlan { get; }
    string PlanPendingText { get; }
    ICommand ReviewPlanCommand { get; }
    ICommand DiscardPlanCommand { get; }
    bool HasSelectedModule { get; }
    string SelectedTitle { get; }
    bool SelectedToneOk { get; }
    bool SelectedToneWarn { get; }
    bool SelectedToneBad { get; }
    string SelectedStatusText { get; }
    string SelectedId { get; }
    string SelectedVersionText { get; }
    bool ShowSelectedReadOnly { get; }
    string SelectedInstalledVersionText { get; }
    string SelectedDesiredVersionText { get; }
    string SelectedEffectiveVersionText { get; }
    string SelectedPackagingText { get; }
    string SelectedSourceText { get; }
    string SelectedTrustText { get; }
    bool HasSelectedOrphanedWork { get; }
    IReadOnlyList<string> SelectedOrphanedWork { get; }
    string SelectedOwnerText { get; }
    string SelectedGenerationText { get; }
    string SelectedReleaseLevelText { get; }
    string SelectedRidText { get; }
    string SelectedHostApiText { get; }
    IReadOnlyList<string> SelectedCapabilities { get; }
    bool HasSelectedCapabilities { get; }
    IReadOnlyList<string> SelectedRequires { get; }
    bool HasSelectedRequires { get; }
    IReadOnlyList<string> SelectedRequiredBy { get; }
    bool HasSelectedRequiredBy { get; }
    bool SelectedResidencyToneOk { get; }
    bool SelectedResidencyToneWarn { get; }
    bool SelectedResidencyToneBad { get; }
    bool SelectedResidencyToneIdle { get; }
    string SelectedResidencyStateText { get; }
    bool SelectedHasResidency { get; }
    string SelectedLeasesText { get; }
    string SelectedReleasableText { get; }
    string SelectedWakeText { get; }
    string SelectedLastUsedText { get; }
    string SelectedLastReleasedText { get; }
    bool HasSelectedBlocked { get; }
    string SelectedBlockedText { get; }
    bool HasSelectedFailure { get; }
    string SelectedFailureText { get; }
    string SelectedDesiredPolicyText { get; }
    string SelectedEffectivePolicyText { get; }
    ICommand OpenPolicyCommand { get; }
    string PolicyHint { get; }
    bool CanEditPolicy { get; }
    string ActionTargetText { get; }
    IReadOnlyList<IServerModuleActionRow> ActionRows { get; }
    bool HasOperation { get; }
    string OperationText { get; }
    bool IsPackagesTab { get; }
    bool IsStoreTab { get; }
    bool IsSecurityTab { get; }
}

public interface IServerModuleFilterChoice
{
    string Label { get; }
}

public interface IServerModuleSelection
{
    bool IsSelected { get; set; }
    string Id { get; }
    string Title { get; }
    bool ToneOk { get; }
    bool ToneWarn { get; }
    bool ToneBad { get; }
    bool ToneIdle { get; }
    string StateText { get; }
    string VersionText { get; }
    bool NeedsRestart { get; }
}

public interface IServerModuleActionRow
{
    string Label { get; }
    string Hint { get; }
    bool HasHint { get; }
    ICommand Command { get; }
    bool Allowed { get; }
}
