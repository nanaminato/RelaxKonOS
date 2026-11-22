using System.Windows.Input;
using Avalonia.Controls;

namespace RelaxKonOS.AppSDK;

public sealed record ComponentTaskDialogPresentation(IComponentTaskDialogModel Model, Control Body, Action? RequestClose);

public interface IComponentTaskDialogModel
{
    bool ShowDraftPanel { get; }
    string DraftSummary { get; }
    string PersistenceNotice { get; }
    ICommand PlanDraftCommand { get; }
    bool CanPlanDraft { get; }
    ICommand DiscardCommand { get; }
    bool HasDrafts { get; }
    IEnumerable<IComponentTaskDraftRow> Drafts { get; }
    bool CanCloseTaskDialog { get; }
    void RemoveDraft(IComponentTaskDraftRow row);
}

public interface IComponentTaskDraftRow
{
    string Summary { get; }
}
