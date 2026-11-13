using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// The frozen plan is submitted from the surface that asked for the decision. Applying keeps the
/// dialog open on failure so the reason appears where the user is looking, and closes it only once
/// the plan has actually left the pending state.
/// </summary>
public partial class ServerModulePlanReviewView : UserControl
{
    private readonly ModalDialog<bool>? _dialog;

    public ServerModulePlanReviewView() => InitializeComponent();
    public ServerModulePlanReviewView(ModalDialog<bool> dialog) : this() => _dialog = dialog;

    private ServerModulesViewModel? Model => DataContext as ServerModulesViewModel;

    private async void ApplyPlan_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await model.ApplyCommand.ExecuteAsync(null);
        if (!model.HasPlan) _dialog?.Close(true);
    }

    private async void ApplyPackage_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await model.ApplyPackagePlanCommand.ExecuteAsync(null);
        if (!model.HasPackagePlan) _dialog?.Close(true);
    }

    /// <summary>"Discard" is the cancel path: the plan is dropped, so a later apply cannot submit it.</summary>
    private void Discard_Click(object? sender, RoutedEventArgs e)
    {
        Model?.DiscardPlanCommand.Execute(null);
        Model?.DiscardPackagePlanCommand.Execute(null);
        _dialog?.Close(false);
    }
}
