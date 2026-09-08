using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Apps.AppInstaller.Views;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.AppInstaller;

/// <summary>Built-in installer for user-reviewed .roapp packages; packages are not signed or sandboxed in v2.</summary>
public sealed class AppInstallerApp : RemoteApplicationBase, IFileOpenApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.appinstaller"), "App Installer", "1.0.0", "📦",
        "Install or update RelaxKonOS application packages.", SupportedFileExtensions: [".roapp"]);

    public override void Activate(AppContext context) => OpenInstaller(context, []);

    /// <summary>Explorer invokes this with a server path, so the package is staged locally before review.</summary>
    public void OpenFile(AppContext context, string path) => OpenInstaller(context, [path]);

    private static void OpenInstaller(AppContext context, IReadOnlyList<string> serverPaths)
    {
        var installer = context.Services.GetService(typeof(AppPackageInstallerService)) as AppPackageInstallerService;
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        if (installer is null)
            return;

        var viewModel = new AppInstallerViewModel(installer);
        var view = new AppInstallerView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("app_installer.title"), view,
            bounds: new Rect(230, 110, 620, 580), iconGlyph: "📦", canResize: true);

        viewModel.RequestLocalPackagesAsync = async () =>
        {
            var topLevel = GetTopLevel();
            if (topLevel is null) return [];
            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("app_installer.select_local_package"),
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(LocalizedText.Get("app_installer.package_file_type")) { Patterns = ["*.roapp"] }],
            });
            return selected.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        };

        viewModel.RequestServerPackagesAsync = async () =>
        {
            if (files is null) return [];
            var result = await context.ShowDialogAsync<IReadOnlyList<string>>(window, LocalizedText.Get("app_installer.select_server_package"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, AllowMultiple: true,
                        Filters: [new ExplorerFileFilter(LocalizedText.Get("app_installer.package_file_filter"), ["*.roapp"])]),
                    paths => dialog.Close(paths))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, GetPickerBounds(window));
            return result ?? [];
        };
        viewModel.ShowMessageAsync = (title, message) => context.ShowDialogAsync<bool>(window, title,
            dialog => CreateMessageView(dialog, message));

        EventHandler<ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            viewModel.Dispose();
        };
        context.WindowManager.WindowClosed += closed;

        if (serverPaths.Count > 0)
            _ = viewModel.QueueServerPackagesAsync(serverPaths);
    }

    private static TopLevel? GetTopLevel() => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        ? desktop.MainWindow
        : null;

    private static Rect GetPickerBounds(RelaxKonOS.WindowManager.ManagedWindow owner)
    {
        var bounds = owner.Info.Bounds;
        return new Rect(bounds.X + 24, bounds.Y + 28, Math.Min(760, Math.Max(480, bounds.Width - 48)),
            Math.Min(520, Math.Max(320, bounds.Height - 56)));
    }

    private static Control CreateMessageView(ModalDialog<bool> dialog, string message)
    {
        var confirm = new Button { Content = LocalizedText.Get("common.ok"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Padding = new Thickness(16, 6) };
        confirm.Click += (_, _) => dialog.Close(true);
        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                confirm,
            },
        };
    }
}
