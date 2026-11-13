using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// Review, grant and revoke standing background permission. Closing is always safe: a prepared
/// consent stays prepared, and only the explicit revoke command removes one.
/// </summary>
public partial class ServerModuleConsentView : UserControl
{
    private readonly ModalDialog<bool>? _dialog;

    public ServerModuleConsentView() => InitializeComponent();
    public ServerModuleConsentView(ModalDialog<bool> dialog) : this() => _dialog = dialog;

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog?.Close(true);
}
