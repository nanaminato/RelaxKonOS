using RelaxKonOS.Client.Services.WorkspaceSettings;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Data;
using Avalonia.Styling;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Apps.Settings.Views;
using RelaxKonOS.Client.Apps.Settings.Views.Pages;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
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
        var explorer = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;

        var viewModel = new SettingsViewModel(settings, settingsClient, session, context.Services.GetRequiredService<WorkspacePreferencesEditor>(), apps, remote, system, registry, developerMode, packages,
            browserClient, networkInspector, wallpapers: wallpapers);
        var view = new SettingsView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("settings.title"), view,
            bounds: new Rect(180, 90, 820, 560),
            iconGlyph: Manifest.IconGlyph);
        _viewModel = viewModel;
        _window = window;
        var outboundProxy = viewModel.Pages.OfType<NetworkPageViewModel>().Single().OutboundProxy;
        outboundProxy.RequestConfirmationAsync = async message =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.outbound_proxy.title"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, result => { confirmed = result; dialog.Close(result); }, LocalizedText.Get("settings.outbound_proxy.confirm_continue")),
            });
            return confirmed;
        };
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
        // The elevation dialog only needs the shared connection check, so every host-settings domain
        // reuses one prompt implementation instead of growing its own copy.
        async Task<bool> AuthorizeHostSettingsAsync(Func<Services.HostSettings.HostSettingsConnection, bool> isCurrent,
            Services.HostSettings.HostSettingsConnection connection, string titleKey,
            Func<string?, string?, Task<RelaxKonOS.Protocol.Privileged.HostElevationResult>> authorize)
        {
            try { return (await authorize(null, null)).Elevated; }
            catch (RelaxKonOSAuthException error) when (error.Type.EndsWith("/elevation-password-required", StringComparison.Ordinal))
            {
                var authorized = await context.WindowManager.ShowSystemDialogAsync<bool>(
                    LocalizedText.Get(titleKey), dialog =>
                    {
                        var password = new Avalonia.Controls.TextBox { PasswordChar = '•', PlaceholderText = LocalizedText.Get("settings.host_time.password") };
                        var errorText = new Avalonia.Controls.TextBlock
                        {
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        };
                        RelaxKonOS.UI.Themes.ThemeResources.Bind(errorText, Avalonia.Controls.TextBlock.ForegroundProperty, "DangerBrush");
                        var submitting = false;
                        const double authorizationActionWidth = 80;
                        const double authorizationActionHeight = 40;
                        var cancel = new Avalonia.Controls.Button
                        {
                            Content = LocalizedText.Get("common.cancel"),
                            Width = authorizationActionWidth,
                            Height = authorizationActionHeight,
                        };
                        cancel.Click += (_, _) => { password.Text = ""; dialog.Cancel(); };
                        var confirm = new Avalonia.Controls.Button
                        {
                            Content = LocalizedText.Get("common.ok"),
                            Width = authorizationActionWidth,
                            Height = authorizationActionHeight,
                            Classes = { "primary" },
                        };
                        confirm.Click += async (_, _) =>
                        {
                            var secret = password.Text ?? "";
                            password.Text = "";
                            if (string.IsNullOrWhiteSpace(secret))
                            {
                                errorText.Text = LocalizedText.Get("settings.host_time.password_required");
                                password.Focus();
                                return;
                            }
                            if (submitting) return;
                            submitting = true;
                            confirm.IsEnabled = cancel.IsEnabled = false;
                            try
                            {
                                var result = await authorize(secret, null);
                                if (!isCurrent(connection)) { dialog.Cancel(); return; }
                                if (result.Elevated) { dialog.Close(true); return; }
                                errorText.Text = LocalizedText.Get("settings.host_time.password_invalid");
                            }
                            catch (RelaxKonOSAuthException retry) when (retry.Type.EndsWith("/elevation-account-not-administrator", StringComparison.Ordinal))
                            {
                                errorText.Text = LocalizedText.Get("settings.host_time.administrator_required");
                            }
                            catch (RelaxKonOSAuthException)
                            {
                                errorText.Text = LocalizedText.Get("settings.host_time.password_invalid");
                            }
                            catch
                            {
                                errorText.Text = LocalizedText.Get("settings.host_time.password_check_failed");
                            }
                            finally
                            {
                                submitting = false;
                                confirm.IsEnabled = cancel.IsEnabled = true;
                                password.Focus();
                            }
                        };
                        return new Avalonia.Controls.StackPanel
                        {
                            Margin = new Avalonia.Thickness(20), Spacing = 10,
                            Children =
                            {
                                new Avalonia.Controls.TextBlock { Text = LocalizedText.Get("settings.host_time.password_prompt"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                                password, errorText,
                                new Avalonia.Controls.StackPanel
                                {
                                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                    Spacing = 8,
                                    Children = { cancel, confirm },
                                }
                            }
                        };
                    }, new Size(420, 210));
                return authorized && isCurrent(connection);
            }
        }
        viewModel.Pages.OfType<TimeLanguagePageViewModel>().Single().HostTime.RequestAuthorizationAsync = connection =>
            AuthorizeHostSettingsAsync(hostTimeService.IsCurrent, connection, "settings.host_time.authorize", (password, administrator) => hostTimeService.AuthorizeAsync(connection, password, administrator));
        var hostIdentityService = context.Services.GetRequiredService<Services.HostSettings.IHostIdentityService>();
        var hostEnvironment = context.Services.GetRequiredService<Services.HostSettings.IHostEnvironmentService>();
        var systemPage = viewModel.Pages.OfType<SystemPageViewModel>().Single();
        systemPage.HostIdentity.RequestAuthorizationAsync = connection =>
            AuthorizeHostSettingsAsync(hostIdentityService.IsCurrent, connection, "settings.hostname.authorize", (password, administrator) => hostIdentityService.AuthorizeAsync(connection, password, administrator));
        var aboutPage = viewModel.Pages.OfType<AboutPageViewModel>().Single();
        aboutPage.RequestOpenUriAsync = uri =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // A missing host handler must not leave the Settings window in an invalid state.
            }
            return Task.CompletedTask;
        };
        aboutPage.RequestCopyTextAsync = async value =>
        {
            var topLevel = AvaloniaApplication.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            if (topLevel?.Clipboard is not null) await topLevel.Clipboard.SetTextAsync(value);
        };
        aboutPage.RequestLegalDocumentAsync = async document =>
        {
            var text = await document.LoadAsync();
            await context.ShowDialogAsync<bool>(window, document.LocalizedTitle, dialog => new LegalTextDialogView
            {
                DataContext = new LegalTextDialogViewModel(document.LocalizedTitle, text, () => dialog.Close(true)),
            }, new Size(760, 620));
        };
        systemPage.RequestEnvironmentVariablesAsync = async () =>
        {
            EnvironmentPageViewModel? editor = null;
            try
            {
                // Detect/load the server environment before creating its editor window.  This
                // prevents the generic page from flashing behind the administrator prompt.
                editor = new EnvironmentPageViewModel(settings, hostEnvironment, session)
                {
                    RequestAuthorizationAsync = async (connection, scope, capability) =>
                    {
                        return await AuthorizeHostSettingsAsync(hostEnvironment.IsCurrent, connection, "settings.environment.authorize",
                            (password, administrator) => hostEnvironment.AuthorizeAsync(connection, scope, capability, password, administrator));
                    },
                };
                await editor.InitializeAsync();
                if (!editor.HasLoadedEnvironment) return;
                await context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.environment.title"), environmentDialog =>
                {
                    editor.RequestClose = () => environmentDialog.Close(false);
                    editor.RequestEnvironmentMutationAsync = async (scope, existing) =>
                        {
                            EnvironmentMutation? mutation = null;
                            var windows = editor.IsWindowsEnvironment;
                            var title = LocalizedText.Get(scope == SettingsScope.HostMachine
                                ? "settings.environment.system_variables" : "settings.environment.user_variables");
                            await environmentDialog.ShowDialogAsync<bool>(title, dialog =>
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
                                var pathEditor = new EnvironmentPathEditor(existing?.RawValue ?? "", isPath, windows ? ";" : ":");
                                var pathEntry = new Avalonia.Controls.TextBox { MaxLength = EnvironmentValidation.MaximumValueLength };
                                var pathList = new Avalonia.Controls.ListBox { ItemsSource = pathEditor.Entries, MinHeight = 230, SelectionMode = Avalonia.Controls.SelectionMode.Single };
                                pathList.Classes.Add("windows-path-editor");
                                pathList.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                                    new Binding(nameof(EnvironmentPathEditor.SelectedEntry)) { Source = pathEditor, Mode = BindingMode.TwoWay });
                                pathList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<EnvironmentPathEntry>((_, _) =>
                                {
                                    var text = new Avalonia.Controls.TextBlock { Margin = new Avalonia.Thickness(8, 4), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                                    text.Bind(Avalonia.Controls.TextBlock.TextProperty, new Binding(nameof(EnvironmentPathEntry.Value)));
                                    return text;
                                });
                                // This dialog is hosted in its own window, so its selected state needs a local style.
                                pathList.Styles.Add(new Avalonia.Styling.Style(selector => selector.Is<Avalonia.Controls.ListBoxItem>().Class(":selected"))
                                {
                                    Setters =
                                    {
                                        new Avalonia.Styling.Setter(Avalonia.Controls.ListBoxItem.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SelectionBackgroundBrush")),
                                        new Avalonia.Styling.Setter(Avalonia.Controls.ListBoxItem.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FocusBorderBrush")),
                                        new Avalonia.Styling.Setter(Avalonia.Controls.ListBoxItem.BorderThicknessProperty, new Avalonia.Thickness(1)),
                                    },
                                });
                                pathEntry.Bind(Avalonia.Controls.TextBox.TextProperty,
                                    new Binding("SelectedEntry.Value") { Source = pathEditor, Mode = BindingMode.TwoWay });
                                pathEntry.Bind(Avalonia.Controls.Control.IsEnabledProperty,
                                    new Binding(nameof(EnvironmentPathEditor.HasSelection)) { Source = pathEditor });
                                Avalonia.Controls.Button? browse = null;
                                var pathButtons = new Avalonia.Controls.StackPanel
                                {
                                    Spacing = 7,
                                    Margin = new Avalonia.Thickness(0, 0, 0, 16),
                                };
                                void AddPathButton(string key, Action action)
                                {
                                    var button = new Avalonia.Controls.Button { Content = LocalizedText.Get(key), MinWidth = 82 };
                                    button.Click += (_, _) => action();
                                    pathButtons.Children.Add(button);
                                }
                                AddPathButton("common.new", () => { pathEditor.Add(); pathEntry.Focus(); });
                                AddPathButton("settings.environment.edit", () => { if (pathEditor.HasSelection) pathEntry.Focus(); });
                                AddPathButton("common.delete", pathEditor.RemoveSelected);
                                AddPathButton("settings.environment.path_up", () =>
                                {
                                    pathEditor.MoveSelected(-1);
                                });
                                AddPathButton("settings.environment.path_down", () =>
                                {
                                    pathEditor.MoveSelected(1);
                                });
                                browse = new Avalonia.Controls.Button { Content = LocalizedText.Get("settings.environment.browse"), IsEnabled = !isPath };
                                if (isPath)
                                    browse.Bind(Avalonia.Controls.Control.IsEnabledProperty,
                                        new Binding(nameof(EnvironmentPathEditor.HasSelection)) { Source = pathEditor });
                                browse.Click += async (_, _) =>
                                {
                                    if (explorer is null) return;
                                    var selected = await dialog.ShowDialogAsync<string?>(LocalizedText.Get("settings.environment.browse"), pickerDialog =>
                                    {
                                        var picker = new ExplorerViewModel(explorer, new ExplorerPickerOptions(ExplorerPickerMode.SelectFolder),
                                            paths => pickerDialog.Close(paths[0]))
                                        {
                                            CancelAction = pickerDialog.Cancel,
                                        };
                                        _ = picker.LoadRootAsync();
                                        return new ExplorerMainView { DataContext = picker };
                                    });
                                    if (string.IsNullOrWhiteSpace(selected)) return;
                                    if (isPath && pathEditor.SelectedEntry is { } entry) entry.Value = selected;
                                    else value.Text = selected;
                                };
                                var cancel = new Avalonia.Controls.Button { Content = LocalizedText.Get("common.cancel"), MinWidth = 88 };
                                cancel.Click += (_, _) => dialog.Cancel();
                                var save = new Avalonia.Controls.Button { Content = LocalizedText.Get("common.save"), MinWidth = 88 };
                                save.Click += (_, _) =>
                                {
                                    var variableName = name.Text?.Trim() ?? "";
                                    var variableValue = isPath ? pathEditor.JoinedValue : value.Text ?? "";
                                    if (!EnvironmentValidation.IsValidName(variableName, windows)
                                        || variableValue.Length > EnvironmentValidation.MaximumValueLength)
                                        return;
                                    mutation = new EnvironmentMutation(variableName, EnvironmentMutationKind.Set, variableValue,
                                        existing?.ValueKind ?? EnvironmentValueKind.String);
                                    dialog.Close(true);
                                };
                                var content = new Avalonia.Controls.StackPanel
                                {
                                    Margin = new Avalonia.Thickness(20, 0, 20, 12), Spacing = 10,
                                };
                                content.Children.Add(new Avalonia.Controls.TextBlock { Text = LocalizedText.Get(isPath ? "settings.environment.path_entries" : "settings.environment.value") });
                                if (isPath)
                                {
                                    var pathEditorLayout = new Avalonia.Controls.Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
                                    pathEditorLayout.Children.Add(pathList);
                                    Avalonia.Controls.Grid.SetColumn(pathButtons, 1);
                                    pathEditorLayout.Children.Add(pathButtons);
                                    content.Children.Add(pathEditorLayout);
                                    content.Children.Add(pathEntry);
                                }
                                else content.Children.Add(value);
                                content.Children.Add(browse);
                                var footer = new Avalonia.Controls.StackPanel
                                {
                                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                    Margin = new Avalonia.Thickness(20, 0, 20, 12), Spacing = 8, Children = { cancel, save },
                                };
                                var nameHeader = new Avalonia.Controls.StackPanel
                                {
                                    Margin = new Avalonia.Thickness(20, 20, 20, 12), Spacing = 10,
                                    Children =
                                    {
                                        new Avalonia.Controls.TextBlock { Text = LocalizedText.Get("settings.environment.name") },
                                        name,
                                    },
                                };
                                var editorLayout = new Avalonia.Controls.Grid { RowDefinitions = new Avalonia.Controls.RowDefinitions("Auto,*,Auto") };
                                editorLayout.Children.Add(nameHeader);
                                var scrollableContent = new Avalonia.Controls.ScrollViewer
                                {
                                    Content = content,
                                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                                };
                                Avalonia.Controls.Grid.SetRow(scrollableContent, 1);
                                editorLayout.Children.Add(scrollableContent);
                                Avalonia.Controls.Grid.SetRow(footer, 2);
                                editorLayout.Children.Add(footer);
                                return editorLayout;
                            }, new Size(560, existing?.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase) == true ? 480 : 330));
                            return mutation;
                        };
                    editor.RequestEnvironmentDeletionConfirmationAsync = async (scope, variable) =>
                        {
                            var confirmed = false;
                            await environmentDialog.ShowDialogAsync<bool>(LocalizedText.Get("settings.environment.delete"), dialog => new ConfirmDialogView
                            {
                                DataContext = new ConfirmDialogViewModel(
                                    LocalizedText.Format("settings.environment.delete_confirm", variable.Name),
                                    result => { confirmed = result; dialog.Close(result); },
                                    LocalizedText.Get("common.delete")),
                            }, new Size(420, 220));
                            return confirmed;
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
        return (segments.Length == 1 && new[] { "system", "account-security", "personalization", "time-language", "network", "apps", "image-mirrors", "default-apps", "developer", "about" }.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
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

/// <summary>Owns the PATH edit dialog's list and current item so both controls stay in sync.</summary>
internal sealed partial class EnvironmentPathEditor : ObservableObject
{
    public System.Collections.ObjectModel.ObservableCollection<EnvironmentPathEntry> Entries { get; }

    [ObservableProperty] private EnvironmentPathEntry? _selectedEntry;

    public bool HasSelection => SelectedEntry is not null;
    public string JoinedValue => string.Join(_separator, Entries.Select(entry => entry.Value));
    private readonly string _separator;

    public EnvironmentPathEditor(string value, bool isPath, string separator)
    {
        _separator = separator;
        Entries = new(isPath
            ? value.Split(separator, StringSplitOptions.None).Select(entry => new EnvironmentPathEntry(entry))
            : Array.Empty<EnvironmentPathEntry>());
    }

    partial void OnSelectedEntryChanged(EnvironmentPathEntry? value) => OnPropertyChanged(nameof(HasSelection));

    public void Add()
    {
        var entry = new EnvironmentPathEntry("");
        Entries.Add(entry);
        SelectedEntry = entry;
    }

    public void RemoveSelected()
    {
        if (SelectedEntry is not { } entry) return;
        var index = Entries.IndexOf(entry);
        if (index < 0) return;
        Entries.RemoveAt(index);
        SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Min(index, Entries.Count - 1)];
    }

    public void MoveSelected(int offset)
    {
        if (SelectedEntry is not { } entry) return;
        var index = Entries.IndexOf(entry);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= Entries.Count) return;
        Entries.Move(index, destination);
    }
}

internal sealed partial class EnvironmentPathEntry(string value) : ObservableObject
{
    [ObservableProperty] private string _value = value;
}
