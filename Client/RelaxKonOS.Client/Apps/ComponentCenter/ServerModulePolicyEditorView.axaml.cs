using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// Draft editor for the residency policy. The result only says "a plan was requested": the policy
/// dialog never submits, and cancelling restores the values the selected module actually reports.
/// </summary>
public partial class ServerModulePolicyEditorView : UserControl
{
    private readonly ModalDialog<bool>? _dialog;

    public ServerModulePolicyEditorView() => InitializeComponent();
    public ServerModulePolicyEditorView(ModalDialog<bool> dialog) : this() => _dialog = dialog;

    private void Preview_Click(object? sender, RoutedEventArgs e) => _dialog?.Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as ServerModulesViewModel)?.ResetPolicyFromSelection();
        _dialog?.Close(false);
    }
}
