using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public partial class ServerModulesView : UserControl
{
    public ServerModulesView() => InitializeComponent();

    private async void InstallPackage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerModulesViewModel model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("server_modules.install_package"),
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
            model.PackageStatus = "server_modules.packages_unavailable";
        }
    }
}
