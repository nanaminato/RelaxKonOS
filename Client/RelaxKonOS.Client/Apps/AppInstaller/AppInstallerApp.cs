using RelaxKonOS.Client.Services;
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
using RelaxKonOS.Deployment;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.AppInstaller;

/// <summary>Built-in installer for user-reviewed .roapp packages; packages are not signed or sandboxed in v2.</summary>
public sealed class AppInstallerApp : RemoteApplicationBase, IFileOpenApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.appinstaller"), "App Installer", "1.0.0", "📦",
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

        var viewModel = new AppInstallerViewModel(installer)
        {
            JointDeployment = context.Services.GetService(typeof(JointAppDeploymentService)) as JointAppDeploymentService,
        };
        var view = new AppInstallerView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("app_installer.title"), view,
            bounds: new Rect(230, 110, 620, 580), iconGlyph: "📦", canResize: true);
        window.CanCloseAsync = () => Task.FromResult(viewModel.CanCloseWindow);

        viewModel.RequestLocalPackagesAsync = async () =>
        {
            var topLevel = GetTopLevel();
            if (topLevel is null) return [];
            var selected = await UsageFilePicker.OpenFilePickerAsync(topLevel.StorageProvider, new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("app_installer.select_local_package"),
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(LocalizedText.Get("app_installer.package_file_type")) { Patterns = ["*.roapp"] }],
            }, UsageMemoryStore.Capture(context), "AppInstaller.package-import");
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
                    paths => dialog.Close(paths), usageMemory: UsageMemoryStore.Capture(context), memoryPurpose: "AppInstaller.package-import")
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
        viewModel.ConfirmUninstallAsync = async row => await context.ShowDialogAsync<bool>(window,
            LocalizedText.Get("app_installer.store.uninstall"), dialog => CreateMessageView(dialog,
                LocalizedText.Format("app_installer.store.confirm_uninstall", row.DisplayName), confirmUninstall: true));
        viewModel.SelectOptionalAsync = requirements =>
            context.ShowDialogAsync<IReadOnlyList<DeploymentRequirement>>(window,
                LocalizedText.Get("app_installer.deployment.optional"), dialog => CreateOptionalSelection(dialog, requirements));
        viewModel.ConfirmDeploymentAsync = async plan => await context.ShowDialogAsync<bool>(window,
            LocalizedText.Get("app_installer.deployment.review"), dialog => CreateDeploymentReview(dialog, plan));

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
        else
            _ = viewModel.OpenAsync();
    }

    private static TopLevel? GetTopLevel() => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        ? desktop.MainWindow
        : null;

    internal static Control CreateOptionalSelection(ModalDialog<IReadOnlyList<DeploymentRequirement>> dialog,
        IReadOnlyList<DeploymentRequirement> requirements)
    {
        var choices = requirements.Select(requirement => new CheckBox
        {
            Content = requirement.Package.Id,
            IsChecked = true,
        }).ToArray();
        return DeploymentDialog(new StackPanel { Children = { new ItemsControl { ItemsSource = choices } } },
            "common.ok", () => dialog.Close(requirements.Where((_, index) => choices[index].IsChecked == true).ToArray()),
            dialog.Cancel);
    }

    internal static Control CreateDeploymentReview(ModalDialog<bool> dialog, DeploymentPlan plan)
    {
        var content = new StackPanel { Spacing = 8 };
        if (plan.PreparedSteps.Length > 0 || plan.OrderedReleases.Any(release => release.ActivationOnly))
            content.Children.Add(new TextBlock
            {
                Text = LocalizedText.Get("app_installer.deployment.enable_review"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        foreach (var release in plan.OrderedReleases)
        {
            var label = release.Satisfied ? "app_installer.deployment.satisfied"
                : release.ActivationOnly ? "app_installer.deployment.enable_installed" : "app_installer.deployment.change";
            content.Children.Add(new TextBlock
            {
                Text = !release.Satisfied && !release.ActivationOnly && release.CurrentVersion is { } before
                    ? LocalizedText.Format("app_installer.deployment.version_change", release.Package.Id, before, release.Version)
                    : LocalizedText.Format(label, release.Package.Id, release.Version),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            var step = plan.PreparedSteps.SingleOrDefault(item => item.Package == release.Package);
            if (step is null) continue;
            foreach (var permission in step.Permissions)
                content.Children.Add(new TextBlock
                {
                    Text = $"{permission.Kind} · {string.Join(", ", permission.Platforms)} · {string.Join(", ", permission.Resources)}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                });
        }
        return DeploymentDialog(content, "common.install", () => dialog.Close(true), dialog.Cancel);
    }

    private static Control DeploymentDialog(Control content, string confirmKey, Action confirm, Action cancel)
    {
        var accept = new Button { Content = LocalizedText.Get(confirmKey) };
        accept.Click += (_, _) => confirm();
        var decline = new Button { Content = LocalizedText.Get("common.cancel") };
        decline.Click += (_, _) => cancel();
        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new ScrollViewer { MaxHeight = 340, Content = content },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { decline, accept } },
            }
        };
    }

    private static Rect GetPickerBounds(RelaxKonOS.WindowManager.ManagedWindow owner)
    {
        var bounds = owner.Info.Bounds;
        return new Rect(bounds.X + 24, bounds.Y + 28, Math.Min(760, Math.Max(480, bounds.Width - 48)),
            Math.Min(520, Math.Max(320, bounds.Height - 56)));
    }

    private static Control CreateMessageView(ModalDialog<bool> dialog, string message, bool confirmUninstall = false)
    {
        var confirm = new Button { Content = LocalizedText.Get(confirmUninstall ? "app_installer.store.uninstall" : "common.ok"), Padding = new Thickness(16, 6) };
        confirm.Click += (_, _) => dialog.Close(true);
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        if (confirmUninstall)
        {
            var cancel = new Button { Content = LocalizedText.Get("common.cancel"), Padding = new Thickness(16, 6) };
            cancel.Click += (_, _) => dialog.Cancel();
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(confirm);
        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons,
            },
        };
    }
}
