using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// Review and decide one frozen privilege plan. Closing the dialog is not a decision: a refused
/// plan stays named on the page until it is explicitly cancelled or applied.
/// </summary>
public partial class ServerModulePrivilegeView : UserControl
{
    private readonly ModalDialog<bool>? _dialog;

    public ServerModulePrivilegeView() => InitializeComponent();
    public ServerModulePrivilegeView(ModalDialog<bool> dialog) : this() => _dialog = dialog;

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog?.Close(true);
}
