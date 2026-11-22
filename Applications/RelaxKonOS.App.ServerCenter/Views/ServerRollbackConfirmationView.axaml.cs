using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Data;
using RelaxKonOS.AppSDK;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.ServerCenter.Views;

public partial class ServerRollbackConfirmationView : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public ServerRollbackConfirmationView(ServerRollbackReview review, ModalDialog<bool> dialog, Func<string, Binding> localize)
    {
        InitializeComponent();
        DataContext = review;
        _dialog = dialog;
        Note.Bind(TextBlock.TextProperty, localize("server_center.rollback_confirm_note"));
        CurrentLabel.Bind(TextBlock.TextProperty, localize("server_center.installation.version"));
        PreviousLabel.Bind(TextBlock.TextProperty, localize("server_center.installation.previous_version"));
        CancelButton.Bind(ContentControl.ContentProperty, localize("common.cancel"));
        ConfirmButton.Bind(ContentControl.ContentProperty, localize("server_center.rollback_confirm"));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Close(false);
    private void Confirm_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
