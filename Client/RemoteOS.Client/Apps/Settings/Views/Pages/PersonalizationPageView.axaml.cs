using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Client.Apps.Settings.ViewModels;
using Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Client.Apps.Settings.Views.Pages;

public partial class PersonalizationPageView : UserControl
{
    public PersonalizationPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PersonalizationPageViewModel vm)
                vm.RequestShellPackageInstallAsync = InstallShellPackageAsync;
        };
    }

    private async Task InstallShellPackageAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;
        var selection = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Select a RemoteOS Shell package folder", AllowMultiple = false });
        var path = selection.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await Client.App.Services.GetRequiredService<ShellCatalog>().InstallAsync(path);
    }
}
