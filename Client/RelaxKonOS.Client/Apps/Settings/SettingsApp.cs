using RelaxKonOS.Client.Services.WorkspaceSettings;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Text.Json;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Apps.Settings.Views;
using RelaxKonOS.Client.Apps.Settings.Views.Pages;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Runtime;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using AvaloniaApplication = Avalonia.Application;

namespace RelaxKonOS.Client.Apps.Settings;

/// <summary>Built-in Settings application — Windows 11 / GNOME 风格的设置中心。
/// 八个分类；用户偏好通过独立 Workspace 服务保存。用户偏好（壁纸/主题/时间格式/语言/区域/默认程序）
/// 持久化到服务端 Workspace（<c>/workspaces/{id}/preferences</c>），多设备登录同一 Workspace 共享。
/// 未登录时仍可打开（仅本地 ShellSettings，不持久化）。</summary>
public sealed class SettingsApp : RemoteApplicationBase, IAppActivationHandler
{
    private SettingsViewModel? _viewModel;
    private ManagedWindow? _window;
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.settings"),
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
        var remote = context.Services.GetRequiredService<IRelaxKonOSClient>();
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
        viewModel.Pages.OfType<AccountSecurityPageViewModel>().Single().RequestOperationAsync = async (operation, configuration, cancellationToken) =>
        {
            AliasOperationDialogViewModel? editor = null;
            CancellationTokenRegistration registration = default;
            try
            {
                return await context.ShowDialogAsync<object?>(window, LocalizedText.Get("settings.account.title"), dialog =>
                {
                    editor = new AliasOperationDialogViewModel(operation, configuration, dialog.Close);
                    registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => { editor.Clear(); dialog.Close(null); }));
                    return new AliasOperationDialogView { DataContext = editor };
                }, new Size(540, 620));
            }
            finally { registration.Dispose(); editor?.Clear(); }
        };
        var hostTimeService = context.Services.GetRequiredService<Services.HostSettings.IHostTimeService>();
        async Task<bool> AuthorizeHostSettingsAsync(Services.HostSettings.HostSettingsConnection connection, string target,
            Func<string?, string?, Task<RelaxKonOS.Protocol.Privileged.HostElevationResult>> authorize)
        {
            try { return (await authorize(null, null)).Elevated; }
            catch (RelaxKonOSAuthException error) when (error.Type.EndsWith("/elevation-password-required", StringComparison.Ordinal))
            {
                var credentials = await context.WindowManager.ShowSystemDialogAsync<(string Password, string? Administrator)?>(
                    LocalizedText.Get("settings.host_time.authorize"), dialog =>
                    {
                        var password = new Avalonia.Controls.TextBox { PasswordChar = '•', PlaceholderText = LocalizedText.Get("settings.host_time.password") };
                        var administrator = new Avalonia.Controls.TextBox { PlaceholderText = LocalizedText.Get("settings.host_time.administrator") };
                        var cancel = new Avalonia.Controls.Button { Content = LocalizedText.Get("common.cancel") };
                        cancel.Click += (_, _) => { password.Text = ""; dialog.Cancel(); };
                        var confirm = new Avalonia.Controls.Button { Content = LocalizedText.Get("common.ok") };
                        confirm.Click += (_, _) =>
                        {
                            var secret = password.Text ?? "";
                            password.Text = "";
                            dialog.Close((secret, string.IsNullOrWhiteSpace(administrator.Text) ? null : administrator.Text));
                        };
                        return new Avalonia.Controls.StackPanel
                        {
                            Margin = new Avalonia.Thickness(20), Spacing = 10,
                            Children =
                            {
                                new Avalonia.Controls.TextBlock { Text = connection.ServerUrl + " · " + target, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                                administrator, password,
                                new Avalonia.Controls.WrapPanel { Children = { cancel, confirm } }
                            }
                        };
                    }, new Size(440, 260));
                if (credentials is not { } value || !hostTimeService.IsCurrent(connection)) return false;
                return (await authorize(value.Password, value.Administrator)).Elevated;
            }
        }
        viewModel.Pages.OfType<TimeLanguagePageViewModel>().Single().HostTime.RequestAuthorizationAsync = connection =>
            AuthorizeHostSettingsAsync(connection, "host/time", (password, administrator) => hostTimeService.AuthorizeAsync(connection, password, administrator));
        var hostEnvironment = context.Services.GetRequiredService<Services.HostSettings.IHostEnvironmentService>();
        var systemPage = viewModel.Pages.OfType<SystemPageViewModel>().Single();
        systemPage.RequestEnvironmentVariablesAsync = async () =>
        {
            EnvironmentPageViewModel? editor = null;
            try
            {
                await context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.environment.title"), dialog =>
                {
                    editor = new EnvironmentPageViewModel(settings, hostEnvironment, session)
                    {
                        RequestClose = () => dialog.Close(false),
                        RequestAuthorizationAsync = async (connection, scope, capability) =>
                        {
                            var target = await hostEnvironment.ResolveTargetAsync(connection, scope);
                            return await AuthorizeHostSettingsAsync(connection, target.ResourceId + " · " + capability,
                                (password, administrator) => hostEnvironment.AuthorizeAsync(connection, scope, capability, password, administrator));
                        },
                        RequestWindowsMutationAsync = async (scope, existing) =>
                        {
                            WindowsEnvironmentMutation? mutation = null;
                            var title = LocalizedText.Get(scope == SettingsScope.HostMachine
                                ? "settings.environment.system_variables" : "settings.environment.user_variables");
                            await context.ShowDialogAsync<bool>(window, title, dialog =>
                            {
                                var name = new Avalonia.Controls.TextBox
                                {
                                    Text = existing?.Name ?? "",
                                    MaxLength = EnvironmentValidation.MaximumNameLength,
                                    IsReadOnly = existing is not null,
                                };
                                var isPath = existing?.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase) == true;
                                var value = new Avalonia.Controls.TextBox
                                {
                                    Text = existing?.RawValue ?? "",
                                    AcceptsReturn = false,
                                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                    MaxLength = EnvironmentValidation.MaximumValueLength,
                                    MinHeight = 32,
                                };
                                var pathEntries = new System.Collections.ObjectModel.ObservableCollection<string>(isPath
                                    ? (existing?.RawValue ?? "").Split(';', StringSplitOptions.None) : Array.Empty<string>());
                                var pathEntry = new Avalonia.Controls.TextBox { MaxLength = EnvironmentValidation.MaximumValueLength };
                                var pathList = new Avalonia.Controls.ListBox { ItemsSource = pathEntries, MinHeight = 230 };
                                pathList.SelectionChanged += (_, _) => pathEntry.Text = pathList.SelectedItem as string ?? "";
                                var pathButtons = new Avalonia.Controls.StackPanel { Spacing = 7 };
                                void AddPathButton(string key, Action action)
                                {
                                    var button = new Avalonia.Controls.Button { Content = LocalizedText.Get(key), MinWidth = 82 };
                                    button.Click += (_, _) => action();
                                    pathButtons.Children.Add(button);
                                }
                                AddPathButton("common.new", () => { pathEntries.Add(pathEntry.Text ?? ""); pathList.SelectedIndex = pathEntries.Count - 1; });
                                AddPathButton("settings.environment.edit", () => { if (pathList.SelectedIndex >= 0) pathEntries[pathList.SelectedIndex] = pathEntry.Text ?? ""; });
                                AddPathButton("common.delete", () => { if (pathList.SelectedIndex >= 0) pathEntries.RemoveAt(pathList.SelectedIndex); });
                                AddPathButton("settings.environment.path_up", () =>
                                {
                                    var index = pathList.SelectedIndex; if (index <= 0) return;
                                    (pathEntries[index - 1], pathEntries[index]) = (pathEntries[index], pathEntries[index - 1]); pathList.SelectedIndex = index - 1;
                                });
                                AddPathButton("settings.environment.path_down", () =>
                                {
                                    var index = pathList.SelectedIndex; if (index < 0 || index >= pathEntries.Count - 1) return;
                                    (pathEntries[index + 1], pathEntries[index]) = (pathEntries[index], pathEntries[index + 1]); pathList.SelectedIndex = index + 1;
                                });
                                var highImpact = new Avalonia.Controls.CheckBox
                                {
                                    Content = LocalizedText.Get("settings.environment.high_impact"),
                                };
                                var cancel = new Avalonia.Controls.Button { Content = LocalizedText.Get("common.cancel"), MinWidth = 88 };
                                cancel.Click += (_, _) => dialog.Cancel();
                                var save = new Avalonia.Controls.Button { Content = LocalizedText.Get("common.save"), MinWidth = 88 };
                                save.Click += (_, _) =>
                                {
                                    var variableName = name.Text?.Trim() ?? "";
                                    var variableValue = isPath ? string.Join(';', pathEntries) : value.Text ?? "";
                                    if (!EnvironmentValidation.IsValidName(variableName, windows: true)
                                        || variableValue.Length > EnvironmentValidation.MaximumValueLength)
                                        return;
                                    mutation = new WindowsEnvironmentMutation(
                                        new EnvironmentMutation(variableName, EnvironmentMutationKind.Set, variableValue,
                                            existing?.ValueKind ?? EnvironmentValueKind.String),
                                        highImpact.IsChecked == true);
                                    dialog.Close(true);
                                };
                                var content = new Avalonia.Controls.StackPanel
                                {
                                    Margin = new Avalonia.Thickness(20), Spacing = 10,
                                };
                                content.Children.Add(new Avalonia.Controls.TextBlock { Text = LocalizedText.Get("settings.environment.name") });
                                content.Children.Add(name);
                                content.Children.Add(new Avalonia.Controls.TextBlock { Text = LocalizedText.Get(isPath ? "settings.environment.path_entries" : "settings.environment.value") });
                                if (isPath)
                                {
                                    var pathEditor = new Avalonia.Controls.Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
                                    pathEditor.Children.Add(pathList);
                                    Avalonia.Controls.Grid.SetColumn(pathButtons, 1);
                                    pathEditor.Children.Add(pathButtons);
                                    content.Children.Add(pathEditor);
                                    content.Children.Add(pathEntry);
                                }
                                else content.Children.Add(value);
                                content.Children.Add(highImpact);
                                var footer = new Avalonia.Controls.StackPanel
                                {
                                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                    Spacing = 8, Children = { cancel, save },
                                };
                                var editorLayout = new Avalonia.Controls.Grid { RowDefinitions = new Avalonia.Controls.RowDefinitions("*,Auto") };
                                editorLayout.Children.Add(new Avalonia.Controls.ScrollViewer
                                {
                                    Content = content,
                                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                                });
                                Avalonia.Controls.Grid.SetRow(footer, 1);
                                editorLayout.Children.Add(footer);
                                return editorLayout;
                            }, new Size(560, existing?.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase) == true ? 480 : 330));
                            return mutation;
                        },
                        RequestWindowsDeletionConfirmationAsync = async (scope, variable) =>
                        {
                            var confirmed = false;
                            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.environment.delete"), dialog => new ConfirmDialogView
                            {
                                DataContext = new ConfirmDialogViewModel(
                                    LocalizedText.Format("settings.environment.delete_confirm", variable.Name),
                                    result => { confirmed = result; dialog.Close(result); },
                                    LocalizedText.Get("common.delete")),
                            }, new Size(420, 220));
                            return confirmed;
                        },
                    };
                    return new EnvironmentPageView { DataContext = editor };
                }, new Size(820, 760));
            }
            finally { editor?.Dispose(); }
        };
        systemPage.RequestPerformanceOptionsAsync = () =>
            context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.performance.title"), dialog => new PerformanceOptionsDialogView
            {
                DataContext = new PerformanceOptionsDialogViewModel(settings, viewModel.Save, () => dialog.Close(true)),
            }, new Size(560, 610));
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
                        Patterns = ["*.relaxkonos-theme.json", "*.json"],
                    },
                ],
            });
            var file = selected.FirstOrDefault();
            if (file is null) return;
            try
            {
                await using var stream = await file.OpenReadAsync();
                var palette = await JsonSerializer.DeserializeAsync<ThemePaletteDto>(stream, RelaxKonOSJsonOptions.Default);
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
                SuggestedFileName = palette.Id + ".relaxkonos-theme.json",
                FileTypeChoices =
                [
                    new FilePickerFileType(LocalizedText.Get("settings.custom_theme"))
                    {
                        Patterns = ["*.relaxkonos-theme.json"],
                    },
                ],
            });
            if (file is null) return;
            try
            {
                await using var stream = await file.OpenWriteAsync();
                await JsonSerializer.SerializeAsync(stream, palette, RelaxKonOSJsonOptions.Default);
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
        if (!uri.Scheme.Equals("relaxkonos", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("settings", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = GetPathSegments(uri);
        return (segments.Length == 1 && new[] { "system", "account-security", "personalization", "time-language", "network", "apps", "image-mirrors", "default-apps", "developer" }.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
               || (segments.Length == 3 && segments[0].Equals("apps", StringComparison.OrdinalIgnoreCase)
                   && segments[2].Equals("permissions", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(segments[1]));
    }

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow)
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;
        var segments = GetPathSegments(request.Uri);
        if (segments.Length == 1)
            viewModel.SelectPage(segments[0]);
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
