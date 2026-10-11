using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

internal partial class FileServicesShareDialogView : UserControl
{
    private readonly FileServicesViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;
    private readonly bool _editing;
    public FileServicesShareDialogView(FileServicesViewModel viewModel, ModalDialog<bool> dialog, bool editing)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        _editing = editing;
        InitializeComponent();
        DataContext = viewModel;
        var initial = (viewModel.ShareName, viewModel.SharePath, viewModel.ShareDescription,
            viewModel.ShareReadOnly, viewModel.ShareEnabled, viewModel.ShareGuestAllowed);
        var permissions = viewModel.SharePermissions.Select(permission => (permission.Principal, permission.SelectedAccess.Value)).ToArray();
        RelaxKonOS.Client.Services.Dialogs.DraftDialogGuard.Attach(dialog, () => !viewModel.CanCloseShareDraft,
            () => initial != (viewModel.ShareName, viewModel.SharePath, viewModel.ShareDescription,
                viewModel.ShareReadOnly, viewModel.ShareEnabled, viewModel.ShareGuestAllowed)
                || !permissions.SequenceEqual(viewModel.SharePermissions.Select(permission => (permission.Principal, permission.SelectedAccess.Value))));
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
    private async void BrowsePath_Click(object? sender, RoutedEventArgs e) => await _viewModel.PickSharePathAsync();
    private void AddPermission_Click(object? sender, RoutedEventArgs e) => _viewModel.AddSharePermission();
    private void RemovePermission_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileSharePermissionEditor permission }) _viewModel.SharePermissions.Remove(permission);
    }
    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.SaveShareAsync(_editing)) _dialog.Close(true);
    }
}
