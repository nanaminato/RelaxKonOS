using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ServerModulePackagesView : UserControl
{
    public ServerModulePackagesView() => InitializeComponent();

    private async void InstallPackage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IServerModulePackages model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = model.Labels("server_modules.install_package"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("RelaxKonOS module") { Patterns = ["*.rkmodule"] }],
            });
            if (files.Count != 1) return;
            await using var stream = await files[0].OpenReadAsync();
            if (stream is not { CanSeek: true }) return;
            await model.InstallPackageAsync(stream, stream.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            model.ReportPackageUnavailable();
        }
    }
}
