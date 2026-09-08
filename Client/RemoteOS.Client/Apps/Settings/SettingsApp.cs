using Client.Services.WorkspaceSettings;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Text.Json;
using Client.Apps.Settings.ViewModels;
using Client.Apps.Settings.Views;
using Client.Apps.Explorer.Dialogs;
using Client.Services;
using Client.Services.Auth;
using Client.Services.AppPermissions;
using Client.Services.Developer;
using Client.Services.Diagnostics;
using Client.Apps.TaskManager;
using Client.Apps.Browser;
using Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Runtime;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Workspace;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;
using AvaloniaApplication = Avalonia.Application;

namespace Client.Apps.Settings;

/// <summary>Built-in Settings application — Windows 11 / GNOME 风格的设置中心。
/// 八个分类；用户偏好通过独立 Workspace 服务保存。用户偏好（壁纸/主题/时间格式/语言/区域/默认程序）
/// 持久化到服务端 Workspace（<c>/workspaces/{id}/preferences</c>），多设备登录同一 Workspace 共享。
/// 未登录时仍可打开（仅本地 ShellSettings，不持久化）。</summary>
public sealed class SettingsApp : RemoteApplicationBase, IAppActivationHandler
{
    private SettingsViewModel? _viewModel;
    private ManagedWindow? _window;
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.settings"),
        DisplayName: "Settings",
        Version: "1.0.0",
        IconGlyph: "⚙️",
        Description: "个性化与系统设置",
        RequestedPermissions: [AppPermissions.DesktopWallpaperWrite],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var settings = context.Services.GetRequiredService<ShellSettings>();
        var session = context.Services.GetRequiredService<IAuthSession>();
        var settingsClient = context.Services.GetRequiredService<IWorkspaceSettingsService>();
        var apps = context.Services.GetRequiredService<ApplicationManager>();
        var remote = context.Services.GetRequiredService<IRemoteOsClient>();
        var system = context.Services.GetRequiredService<ITaskManagerClient>();
        var registry = context.Services.GetRequiredService<DefaultAppRegistry>();
        var permissions = context.Services.GetRequiredService<IAppPermissionManager>();
        var appData = context.Services.GetRequiredService<IAppDataManager>();
        var localization = context.Services.GetRequiredService<LocalizationService>();
        var developerMode = context.Services.GetRequiredService<DeveloperModeService>();
        var packages = context.Services.GetRequiredService<DeveloperPackageManager>();
        var networkInspector = context.Services.GetRequiredService<NetworkInspectorWindowService>();
        var wallpapers = context.Services.GetRequiredService<WallpaperService>();
        var browserClient = context.Services.GetRequiredService<IBrowserClient>();
        var imageMirrors = context.Services.GetRequiredService<IImageMirrorClient>();

        var viewModel = new SettingsViewModel(settings, settingsClient, session, context.Services.GetRequiredService<WorkspacePreferencesEditor>(), apps, remote, system, registry, developerMode, packages,
            browserClient, imageMirrors, networkInspector, wallpapers: wallpapers);
        var view = new SettingsView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("settings.title"), view,
            bounds: new Rect(180, 90, 820, 560),
            iconGlyph: Manifest.IconGlyph);
        _viewModel = viewModel;
        _window = window;
        var appsPage = viewModel.Pages.OfType<AppsPageViewModel>().Single();
        var personalizationPage = viewModel.Pages.OfType<PersonalizationPageViewModel>().Single();
        personalizationPage.RequestCustomWallpaperAsync = async () =>
        {
            var topLevel = AvaloniaApplication.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            if (topLevel is null) return;
            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("settings.wallpaper.choose_image"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(LocalizedText.Get("settings.wallpaper"))
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif"],
                    },
                ],
            });
            var file = selected.FirstOrDefault();
            if (file is null) return;
            try
            {
                await using var stream = await file.OpenReadAsync();
                if (stream.CanSeek
                    && (stream.Length < WorkspaceWallpaperUploadLimits.MinFileBytes
                        || stream.Length > WorkspaceWallpaperUploadLimits.MaxFileBytes))
                {
                    throw new InvalidOperationException(LocalizedText.Format(
                        "settings.wallpaper.size_limit", WorkspaceWallpaperUploadLimits.MaxFileMegabytes));
                }
                if (stream.CanSeek) stream.Position = 0;
                await wallpapers.UploadAndApplyAsync(stream, file.Name);
            }
            catch (OperationCanceledException)
            {
                // The picker, stream, or request was cancelled; no user-facing failure is needed.
            }
            catch (Exception ex)
            {
                await context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.wallpaper"), dialog => new ConfirmDialogView
                {
                    DataContext = new ConfirmDialogViewModel(
                        LocalizedText.Format("settings.wallpaper.upload_failed", ex.Message),
                        result => dialog.Close(result),
                        LocalizedText.Get("common.ok")),
                });
            }
        };
        personalizationPage.RequestThemeImportAsync = async () =>
        {
            var topLevel = AvaloniaApplication.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            if (topLevel is null) return;
            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("settings.theme_import"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(LocalizedText.Get("settings.custom_theme"))
                    {
                        Patterns = ["*.remoteos-theme.json", "*.json"],
                    },
                ],
            });
            var file = selected.FirstOrDefault();
            if (file is null) return;
            try
            {
                await using var stream = await file.OpenReadAsync();
                var palette = await JsonSerializer.DeserializeAsync<ThemePaletteDto>(stream, RemoteOsJsonOptions.Default);
                if (!personalizationPage.TryImportCustomPalette(palette, out var error))
                {
                    await ShowThemeMessageAsync(context, window, error!);
                }
            }
            catch (OperationCanceledException)
            {
                // The picker or stream was cancelled; there is no state to recover.
            }
            catch (Exception ex)
            {
                await ShowThemeMessageAsync(context, window, LocalizedText.Format("settings.theme_import.failed", ex.Message));
            }
        };
        personalizationPage.RequestThemeExportAsync = async palette =>
        {
            var topLevel = AvaloniaApplication.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            if (topLevel is null) return;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizedText.Get("settings.theme_export"),
                SuggestedFileName = palette.Id + ".remoteos-theme.json",
                FileTypeChoices =
                [
                    new FilePickerFileType(LocalizedText.Get("settings.custom_theme"))
                    {
                        Patterns = ["*.remoteos-theme.json"],
                    },
                ],
            });
            if (file is null) return;
            try
            {
                await using var stream = await file.OpenWriteAsync();
                await JsonSerializer.SerializeAsync(stream, palette, RemoteOsJsonOptions.Default);
            }
            catch (OperationCanceledException)
            {
                // A cancelled write has no user-actionable error.
            }
            catch (Exception ex)
            {
                await ShowThemeMessageAsync(context, window, LocalizedText.Format("settings.theme_export.failed", ex.Message));
            }
        };
        personalizationPage.RequestThemeDeletionConfirmationAsync = async palette =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.theme_delete"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(
                    LocalizedText.Format("settings.theme_delete.confirmation", palette.Name),
                    result => { confirmed = result; dialog.Close(result); },
                    LocalizedText.Get("settings.theme_delete")),
            });
            return confirmed;
        };
        appsPage.RequestPermissionEditorAsync = async app =>
        {
            AppPermissionDialogViewModel? dialogViewModel = null;
            await context.ShowDialogAsync<bool>(
                window,
                LocalizedText.Format("settings.apps.permissions_title", app.DisplayName),
                dialog => new AppPermissionDialogView
                {
                    DataContext = dialogViewModel = new AppPermissionDialogViewModel(app, permissions, localization, dialog.Close),
                },
                new Size(640, 560));
            dialogViewModel?.Dispose();
        };
        appsPage.RequestUninstallConfirmationAsync = async app =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Format("settings.apps.uninstall_title", app.DisplayName), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(
                    LocalizedText.Format("settings.apps.uninstall_confirmation", app.DisplayName),
                    result => { confirmed = result; dialog.Close(result); },
                    LocalizedText.Get("settings.uninstall")),
            });
            return confirmed;
        };
        appsPage.RequestClearDataAsync = async app =>
        {
            var options = await context.ShowDialogAsync<AppDataClearOptions?>(window,
                LocalizedText.Format("settings.apps.clear_data_title", app.DisplayName), dialog => new AppDataClearDialogView
                {
                    DataContext = new AppDataClearDialogViewModel(app, dialog.Close),
                }, new Size(520, 390));
            return options is null ? null : await appData.ClearAsync(app.Id, options);
        };

        EventHandler<ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            if (ReferenceEquals(_window, window))
            {
                _window = null;
                _viewModel = null;
            }
            viewModel.Dispose();
        };
        context.WindowManager.WindowClosed += closed;

        // 窗口打开后异步加载服务端偏好。
        _ = viewModel.InitializeAsync();
    }

    public bool CanHandleActivation(Uri uri)
    {
        if (!uri.Scheme.Equals("remoteos", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("settings", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = GetPathSegments(uri);
        return (segments.Length == 1 && (segments[0].Equals("personalization", StringComparison.OrdinalIgnoreCase)
                                       || segments[0].Equals("apps", StringComparison.OrdinalIgnoreCase)))
               || (segments.Length == 3 && segments[0].Equals("apps", StringComparison.OrdinalIgnoreCase)
                   && segments[2].Equals("permissions", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(segments[1]));
    }

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow)
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;
        var segments = GetPathSegments(request.Uri);
        if (segments.Length == 1 && segments[0].Equals("personalization", StringComparison.OrdinalIgnoreCase))
            viewModel.SelectPersonalizationPage();
        else if (segments.Length == 1 && segments[0].Equals("apps", StringComparison.OrdinalIgnoreCase))
            viewModel.SelectApplicationsPage();
        else if (segments.Length == 3 && segments[0].Equals("apps", StringComparison.OrdinalIgnoreCase)
                 && segments[2].Equals("permissions", StringComparison.OrdinalIgnoreCase))
            _ = viewModel.SelectApplicationPermissionsAsync(segments[1]);
    }

    private static string[] GetPathSegments(Uri uri) => uri.AbsolutePath
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.UnescapeDataString)
        .ToArray();

    private static Task ShowThemeMessageAsync(AppContext context, ManagedWindow window, string message) =>
        context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.custom_theme"), dialog => new ConfirmDialogView
        {
            DataContext = new ConfirmDialogViewModel(message, result => dialog.Close(result), LocalizedText.Get("common.ok")),
        });
}
