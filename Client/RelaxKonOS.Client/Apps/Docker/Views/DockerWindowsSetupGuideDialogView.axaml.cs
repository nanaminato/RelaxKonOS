using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Docker.Views;

/// <summary>In-product guidance for a Windows host that runs Linux containers through Docker Desktop.</summary>
internal partial class DockerWindowsSetupGuideDialogView : UserControl
{
    private readonly DockerManagerViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public DockerWindowsSetupGuideDialogView(DockerManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        _dialog.Close(true);
        _viewModel.RefreshCommand.Execute(null);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
