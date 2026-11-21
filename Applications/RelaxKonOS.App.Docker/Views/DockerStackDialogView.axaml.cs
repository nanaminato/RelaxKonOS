using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.AppSDK;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Docker.Views;

internal partial class DockerStackDialogView : UserControl
{
    private readonly DockerManagerViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public DockerStackDialogView(DockerManagerViewModel viewModel, ModalDialog<bool> dialog, IExternalAppWindowHandle owner)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
        var initialName = viewModel.StackName;
        var initialYaml = viewModel.ComposeYaml;
        dialog.CanCancelAsync = async () =>
        {
            if (viewModel.IsLoading) return false;
            if (viewModel.StackName == initialName && viewModel.ComposeYaml == initialYaml) return true;
            var text = viewModel.Labels;
            var discard = await owner.ConfirmNestedAsync(dialog, text.Get("common.discard_title"),
                text.Get("common.discard_message"), text.Get("common.discard"));
            return discard && !viewModel.IsLoading;
        };
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
    private async void Deploy_Click(object? sender, RoutedEventArgs e) { if (await _viewModel.TryDeployStackAsync()) _dialog.Close(true); }
}
