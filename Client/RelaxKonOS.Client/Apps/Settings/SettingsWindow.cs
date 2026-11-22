using RelaxKonOS.App.Settings;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.App.Settings.Views;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using System.Diagnostics;
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
/// SSH 桌面使用独立本地个性化存储，不访问服务端设置。</summary>
public sealed class SettingsWindow(IServiceProvider services) : ISettingsWindow
{
    private SettingsViewModel? _viewModel;
    private ManagedWindow? _window;
    public void Open(Func<ISettingsWorkspace, ManagedWindow> showWorkspace)
    {
        var context = new AppContext(SettingsPackageApp.Id, services.GetRequiredService<IWindowManager>(), services);
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
        var browserClient = context.Services.GetRequiredService<IBrowserData>();
        var explorer = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;

        var viewModel = new SettingsViewModel(settings, settingsClient, session, context.Services.GetRequiredService<WorkspacePreferencesEditor>(), apps, remote, system, registry, developerMode, packages,
            browserClient, networkInspector, wallpapers: wallpapers, sshDesktop: context.Services.GetRequiredService<Services.ServerCenter.SshDesktopSession>());
        ManagedWindow window;
        try
        {
            window = showWorkspace(viewModel);
        }
        catch
        {
            viewModel.Dispose();
            throw;
        }
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
        async Task<bool> AuthorizeHostSettingsAsync(Func<HostSettingsConnection, bool> isCurrent,
            HostSettingsConnection connection, string titleKey,
            Func<string?, string?, Task<RelaxKonOS.Protocol.Privileged.HostElevationResult>> authorize)
        {
            try { return (await authorize(null, null)).Elevated; }
            catch (RelaxKonOSAuthException error) when (error.Type.EndsWith("/elevation-password-required", StringComparison.Ordinal))
            {
                var usageMemory = UsageMemoryStore.Capture(context);
                var defaultAdministrator = usageMemory.Administrator ?? string.Empty;
                var authorized = await context.WindowManager.ShowSystemDialogAsync<bool>(
                    LocalizedText.Get(titleKey), dialog =>
                    {
                        var account = new Avalonia.Controls.TextBox
                        {
                            Text = defaultAdministrator,
                            PlaceholderText = LocalizedText.Get("settings.host_time.account"),
                        };
                        var password = new Avalonia.Controls.TextBox { PasswordChar = '•', PlaceholderText = LocalizedText.Get("settings.host_time.password") };
                        password.AttachedToVisualTree += (_, _) => { if (!string.IsNullOrEmpty(defaultAdministrator)) password.Focus(); };
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
                            if (!usageMemory.IsCurrent || !isCurrent(connection)) { password.Text = ""; dialog.Cancel(); return; }
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
                                var selectedAdministrator = account.Text?.Trim();
                                var result = await authorize(secret,
                                    string.IsNullOrWhiteSpace(selectedAdministrator) ? defaultAdministrator : selectedAdministrator);
                                if (!isCurrent(connection)) { dialog.Cancel(); return; }
                                if (result.Elevated) { usageMemory.RememberAdministrator(selectedAdministrator); dialog.Close(true); return; }
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
                                account, password, errorText,
                                new Avalonia.Controls.StackPanel
                                {
                                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                    Spacing = 8,
                                    Children = { cancel, confirm },
                                }
                            }
                        };
                    }, new Size(420, 260));
                return authorized && isCurrent(connection);
            }
        }
        viewModel.Pages.OfType<TimeLanguagePageViewModel>().Single().HostTime.RequestAuthorizationAsync = connection =>
            AuthorizeHostSettingsAsync(hostTimeService.IsCurrent, connection, "settings.host_time.authorize", (password, administrator) => hostTimeService.AuthorizeAsync(connection, password, administrator));
        // Failures are reported once, as a prompt; the page deliberately keeps no operation-details surface.
        viewModel.Pages.OfType<TimeLanguagePageViewModel>().Single().HostTime.RequestReportProblemAsync = message =>
            context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.host_time.title"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, result => dialog.Close(result), LocalizedText.Get("common.ok")),
            });
        var hostNetworkService = context.Services.GetRequiredService<Services.HostSettings.IHostNetworkService>();
        viewModel.Pages.OfType<NetworkPageViewModel>().Single().HostNetwork.RequestAuthorizationAsync = connection =>
            AuthorizeHostSettingsAsync(hostNetworkService.IsCurrent, connection, "settings.network.authorize", (password, administrator) => hostNetworkService.AuthorizeAsync(connection, password, administrator));
        var hostIdentityService = context.Services.GetRequiredService<Services.HostSettings.IHostIdentityService>();
        var hostEnvironment = context.Services.GetRequiredService<Services.HostSettings.IHostEnvironmentService>();
        var systemPage = viewModel.Pages.OfType<SystemPageViewModel>().Single();
        systemPage.RequestServerModules = () =>
        {
            if (session.State == AuthSessionState.Authenticated && session.ServiceId is { } host)
                context.Activations.Activate(RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterNavigation.ServerModules(host));
        };
        systemPage.RequestServerHttpsAsync = async () =>
        {
            var certificates = new RelaxKonOS.App.Certificates.CertificateManagerViewModel(
                context.Services.GetRequiredService<RelaxKonOS.AppSDK.IServerCertificates>(), context.Permissions,
                context.Services.GetRequiredService<ISystemLanguage>());
            await certificates.StartAsync();
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.server_https.title"),
                dialog => new ServerHttpsDialogView { DataContext = certificates }, new Size(660, 480));
        };
        systemPage.ClearUsageMemoryAction = () => context.Services.GetRequiredService<UsageMemoryStore>().Clear(session);
        systemPage.HostIdentity.RequestAuthorizationAsync = connection =>
            AuthorizeHostSettingsAsync(hostIdentityService.IsCurrent, connection, "settings.hostname.authorize", (password, administrator) => hostIdentityService.AuthorizeAsync(connection, password, administrator));
        // Failures are reported once, as a prompt; the page deliberately keeps no operation-details surface.
        systemPage.HostIdentity.RequestReportProblemAsync = message =>
            context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.hostname"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, result => dialog.Close(result), LocalizedText.Get("common.ok")),
            });
        var aboutPage = viewModel.Pages.OfType<AboutPageViewModel>().Single().Content;
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
            var title = localization.Get(document.TitleKey, document.TitleKey);
            await context.ShowDialogAsync<bool>(window, title, dialog => new LegalTextDialogView
            {
                DataContext = new LegalTextDialogViewModel(title, text, LocalizedText.Get("common.close"), () => dialog.Close(true)),
            }, new Size(760, 620));
        };
        systemPage.RequestWorkspaceEnvironmentAsync = async () =>
        {
            using var editor = new WorkspaceEnvironmentEditorViewModel(context.Services.GetRequiredService<IWorkspaceEnvironmentClient>(), session);
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.workspace_environment.title"), dialog =>
            {
                editor.RequestClose = () => dialog.Close(false);
                _ = editor.ReloadCommand.ExecuteAsync(null);
                return new WorkspaceEnvironmentEditorView { DataContext = editor };
            }, new Size(680, 640));
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
                                return EnvironmentVariableEditor.Create(existing, windows, key => LocalizedText.Get(key),
                                    async () =>
                                    {
                                        if (explorer is null) return null;
                                        return await dialog.ShowDialogAsync<string?>(LocalizedText.Get("settings.environment.browse"), pickerDialog =>
                                        {
                                            var picker = new ExplorerViewModel(explorer, new ExplorerPickerOptions(ExplorerPickerMode.SelectFolder),
                                                paths => pickerDialog.Close(paths[0]), usageMemory: UsageMemoryStore.Capture(context), memoryPurpose: "Settings.environment-folder")
                                            {
                                                CancelAction = pickerDialog.Cancel,
                                            };
                                            _ = picker.LoadRootAsync();
                                            return new ExplorerMainView { DataContext = picker };
                                        });
                                    },
                                    dialog.Cancel,
                                    value => { mutation = value; dialog.Close(true); });
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
                DataContext = new PerformanceOptionsDialogViewModel(
                    () => new WindowEffects(settings.ShowWindowShadows, settings.ShowWindowContentsWhileDragging, settings.ShowTaskbarWindowPreviews),
                    effects =>
                    {
                        settings.ShowWindowShadows = effects.ShowShadows;
                        settings.ShowWindowContentsWhileDragging = effects.ShowWindowContents;
                        settings.ShowTaskbarWindowPreviews = effects.ShowThumbnails;
                        viewModel.Save();
                    },
                    () => dialog.Close(true),
                    key => LocalizedText.Get(key)),
            }, new Size(560, 610));
        var appsPage = viewModel.Pages.OfType<AppsPageViewModel>().Single();
        var personalizationPage = viewModel.Pages.OfType<PersonalizationPageViewModel>().Single();
        personalizationPage.RequestCustomWallpaperAsync = async () =>
        {
            var topLevel = AvaloniaApplication.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            if (topLevel is null) return;
            var selected = await UsageFilePicker.OpenFilePickerAsync(topLevel.StorageProvider, new FilePickerOpenOptions
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
            }, UsageMemoryStore.Capture(context), "Settings.wallpaper");
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
            var selected = await UsageFilePicker.OpenFilePickerAsync(topLevel.StorageProvider, new FilePickerOpenOptions
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
            }, UsageMemoryStore.Capture(context), "Settings.theme-import");
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
            var file = await UsageFilePicker.SaveFilePickerAsync(topLevel.StorageProvider, new FilePickerSaveOptions
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
            }, UsageMemoryStore.Capture(context), "Settings.theme-export");
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
        viewModel.Pages.OfType<ApplicationPermissionsPageViewModel>().Single().CreateEditor =
            (app, complete) => new AppPermissionDialogViewModel(app, permissions, localization, complete);
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
            var title = LocalizedText.Format("settings.apps.clear_data_title", app.DisplayName);
            var selection = await context.ShowDialogAsync<AppDataClearSelection?>(window,
                title, dialog => new AppDataClearDialogView
                {
                    DataContext = new AppDataClearDialogViewModel(title, key => LocalizedText.Get(key), dialog.Close),
                }, new Size(520, 390));
            return selection is null ? null : await appData.ClearAsync(app.Id,
                new AppDataClearOptions(selection.ClearPermissionDecisions, selection.ClearServerData));
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

    public void Navigate(string route) => _viewModel?.SelectPage(route);

    public void OpenEnvironment()
    {
        if (_viewModel is not { } viewModel) return;
        viewModel.SelectPage("system");
        _ = viewModel.Pages.OfType<SystemPageViewModel>().Single().OpenEnvironmentVariablesCommand.ExecuteAsync(null);
    }

    public void OpenApplicationPermissions(string appId)
    {
        if (_viewModel is { } viewModel)
            _ = viewModel.SelectApplicationPermissionsAsync(appId);
    }

    private static Task ShowThemeMessageAsync(AppContext context, ManagedWindow window, string message) =>
        context.ShowDialogAsync<bool>(window, LocalizedText.Get("settings.custom_theme"), dialog => new ConfirmDialogView
        {
            DataContext = new ConfirmDialogViewModel(message, result => dialog.Close(result), LocalizedText.Get("common.ok")),
        });
}
