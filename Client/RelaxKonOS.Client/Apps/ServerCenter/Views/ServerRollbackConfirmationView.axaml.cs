using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

public partial class ServerRollbackConfirmationView : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public ServerRollbackConfirmationView(ServerRollbackReview review, ModalDialog<bool> dialog)
    {
        InitializeComponent();
        DataContext = review;
        _dialog = dialog;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Close(false);
    private void Confirm_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
