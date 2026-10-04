using RelaxKonOS.Client.Services;
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

        var selected = await UsageFilePicker.OpenFilePickerAsync(topLevel.StorageProvider, new FilePickerOpenOptions
        {
            Title = _viewModel.ChooseBundleText,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(_viewModel.BundleFileTypeText) { Patterns = ["*.zip"] }]
        }, UsageMemoryStore.CaptureServices(App.Services), "ServerInstallationWizardView.axaml.0");
        _viewModel.SetLocalBundle(selected.FirstOrDefault()?.TryGetLocalPath());
    }

    private async void ChooseCertificate_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;
        var selected = await UsageFilePicker.OpenFilePickerAsync(topLevel.StorageProvider, new FilePickerOpenOptions
        {
            Title = _viewModel.ChooseCertificateText,
            AllowMultiple = false,
            FileTypeFilter = _viewModel.IsPemCertificate
                ? [new FilePickerFileType("PEM certificate") { Patterns = ["*.pem", "*.crt", "*.cer"] }]
                : [new FilePickerFileType("PFX certificate") { Patterns = ["*.pfx", "*.p12"] }]
        }, UsageMemoryStore.CaptureServices(App.Services), "ServerInstallationWizardView.axaml.1");
        _viewModel.SetCertificate(selected.FirstOrDefault()?.TryGetLocalPath());
    }

    private async void ChoosePrivateKey_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;
        var selected = await UsageFilePicker.OpenFilePickerAsync(topLevel.StorageProvider, new FilePickerOpenOptions
        {
            Title = _viewModel.ChoosePrivateKeyText,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PEM private key") { Patterns = ["*.key", "*.pem"] }]
        }, UsageMemoryStore.CaptureServices(App.Services), "ServerInstallationWizardView.axaml.2");
        _viewModel.SetCertificatePrivateKey(selected.FirstOrDefault()?.TryGetLocalPath());
    }
}
