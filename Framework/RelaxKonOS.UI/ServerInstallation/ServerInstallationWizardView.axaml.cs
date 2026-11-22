using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace RelaxKonOS.UI.ServerInstallation;

public partial class ServerInstallationWizardView : UserControl
{
    private readonly ServerInstallationWizardViewModel _viewModel;
    private readonly ServerInstallationFilePicker _pickFile;
    private long _pickerVersion;

    public ServerInstallationWizardView(ServerInstallationWizardViewModel viewModel, ServerInstallationFilePicker pickFile)
    {
        _viewModel = viewModel;
        _pickFile = pickFile;
        InitializeComponent();
        DataContext = viewModel;
        DetachedFromVisualTree += (_, _) => _pickerVersion++;
    }

    private async void ChooseBundle_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;
        var source = _viewModel.SelectedSource;
        var version = ++_pickerVersion;

        var selected = await _pickFile(topLevel.StorageProvider, new FilePickerOpenOptions
        {
            Title = _viewModel.ChooseBundleText,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(_viewModel.BundleFileTypeText) { Patterns = ["*.zip"] }]
        }, "ServerInstallationWizardView.axaml.0");
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && CanAcceptSelection(topLevel, version) && _viewModel.SelectedSource == source)
            _viewModel.SetLocalBundle(path);
    }

    private async void ChooseCertificate_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;
        var format = _viewModel.SelectedCertificateFormat;
        var version = ++_pickerVersion;
        var selected = await _pickFile(topLevel.StorageProvider, new FilePickerOpenOptions
        {
            Title = _viewModel.ChooseCertificateText,
            AllowMultiple = false,
            FileTypeFilter = _viewModel.IsPemCertificate
                ? [new FilePickerFileType("PEM certificate") { Patterns = ["*.pem", "*.crt", "*.cer"] }]
                : [new FilePickerFileType("PFX certificate") { Patterns = ["*.pfx", "*.p12"] }]
        }, "ServerInstallationWizardView.axaml.1");
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && CanAcceptSelection(topLevel, version) && _viewModel.SelectedCertificateFormat == format)
            _viewModel.SetCertificate(path);
    }

    private async void ChoosePrivateKey_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;
        var version = ++_pickerVersion;
        var selected = await _pickFile(topLevel.StorageProvider, new FilePickerOpenOptions
        {
            Title = _viewModel.ChoosePrivateKeyText,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PEM private key") { Patterns = ["*.key", "*.pem"] }]
        }, "ServerInstallationWizardView.axaml.2");
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && CanAcceptSelection(topLevel, version) && _viewModel.IsPemCertificate)
            _viewModel.SetCertificatePrivateKey(path);
    }

    private bool CanAcceptSelection(TopLevel owner, long version)
        => version == _pickerVersion && !_viewModel.IsBusy && owner.IsVisible && ReferenceEquals(TopLevel.GetTopLevel(this), owner);
}

public delegate Task<IReadOnlyList<IStorageFile>> ServerInstallationFilePicker(
    IStorageProvider storage, FilePickerOpenOptions options, string purpose);
