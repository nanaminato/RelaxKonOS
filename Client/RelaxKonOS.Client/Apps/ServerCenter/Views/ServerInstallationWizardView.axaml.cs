using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.ViewModels.ServerCenter;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

public partial class ServerInstallationWizardView : UserControl
{
    private readonly ServerInstallationWizardViewModel _viewModel;

    public ServerInstallationWizardView(ServerInstallationWizardViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void ChooseBundle_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _viewModel.ChooseBundleText,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(_viewModel.BundleFileTypeText) { Patterns = ["*.zip"] }]
        });
        _viewModel.SetLocalBundle(selected.FirstOrDefault()?.TryGetLocalPath());
    }
}
