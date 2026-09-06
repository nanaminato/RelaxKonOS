using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Client.Apps.Explorer.Dialogs;
using Client.Apps.Explorer.ViewModels;
using Client.Apps.Explorer.Views;
using Client.Localization;
using Client.Apps.Settings;
using Client.Services;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Input;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.Files;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;
using Rect = RemoteOS.Core.Primitives.Rect;
using Size = RemoteOS.Core.Primitives.Size;

namespace Client.Apps.Explorer;

/// <summary>Built-in RemoteExplorer — file manager for the remote host OS.
/// UI 结构移植自 Jaya File Manager (BSD-3)：导航树 + Explorer 网格 + 地址栏 + 工具栏 + 状态栏。
/// 所有文件操作经 <see cref="IExplorerClient"/> 调用 Server 端 REST API（JWT via <see cref="IAuthSession"/>）。
/// 未登录时弹提示窗；服务端以宿主 OS 进程身份执行 IO，复用宿主用户/权限（不另建 ACL）。</summary>
public sealed class ExplorerApp : RemoteApplicationBase, IAppActivationHandler
{
    private readonly Dictionary<ManagedWindow, ExplorerViewModel> _windows = [];
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.explorer"),
        DisplayName: "RemoteExplorer",
        Version: "1.0.0",
        IconGlyph: "📁",
        Description: "远端文件管理器",
        RequestedPermissions: [AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite]);

    public override void Activate(AppContext context) => OpenExplorer(context, null);

    public bool CanHandleActivation(Uri uri) =>
        uri.Scheme.Equals("remoteos", StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("explorer", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/open", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(QueryValue(uri, "path"));

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow)
    {
        var path = QueryValue(request.Uri, "path");
        // For multi-window apps the runtime creates the new window before invoking the
        // handler. Reuse that window instead of opening a duplicate Explorer instance.
        if (existingWindow is not null && _windows.TryGetValue(existingWindow, out var viewModel))
        {
            _ = viewModel.NavigateToAsync(path);
            return;
        }
        OpenExplorer(context, path);
    }

    private void OpenExplorer(AppContext context, string? initialPath)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;

        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            var stub = new TextBlock
            {
                Text = LocalizedText.Get("explorer.login_required"),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
            context.ShowWindow(LocalizedText.Get("application.remoteos.explorer.display_name"), stub,
                bounds: new Rect(200, 160, 460, 180),
                iconGlyph: Manifest.IconGlyph,
                canResize: false, canMinimize: false, canMaximize: false);
            return;
        }

        var clipboard = context.Services.GetService(typeof(IRemoteFileClipboard)) as IRemoteFileClipboard;
        var viewModel = new ExplorerViewModel(client, fileClipboard: clipboard);
        WireDialogs(context, viewModel, client);
        var view = new ExplorerMainView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.explorer.display_name"), view,
            bounds: new Rect(80, 60, 960, 640),
            iconGlyph: Manifest.IconGlyph);
        _windows[window] = viewModel;
        viewModel.CloseAction = () => Dispatcher.UIThread.Post(() =>
        {
            _windows.Remove(window);
            context.WindowManager.Close(window);
        });
        window.KeyDown += (_, e) =>
        {
            if ((e.Key == RemoteKey.Letter('L') && e.Modifiers == RemoteKeyModifiers.Control)
                || (e.Key == RemoteKey.Letter('D') && e.Modifiers == RemoteKeyModifiers.Alt))
            {
                view.FocusAddressBox();
                e.Handled = true;
                return;
            }

            if (e.Key == RemoteKey.Letter('F') && e.Modifiers == RemoteKeyModifiers.Control)
            {
                view.FocusSearchBox();
                e.Handled = true;
                return;
            }
            // Text editing owns Delete / clipboard shortcuts; never mutate remote files here.
            if (view.IsTextEditing || viewModel.IsBusy) return;

            _ = WindowShortcut.TryExecute(e, RemoteKey.Left, RemoteKeyModifiers.Alt, viewModel.GoBackCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Right, RemoteKeyModifiers.Alt, viewModel.GoForwardCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Up, RemoteKeyModifiers.Alt, viewModel.GoUpCommand)
                || WindowShortcut.TryExecute(e, new RemoteKey("F5"), RemoteKeyModifiers.None, viewModel.RefreshCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('N'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift, viewModel.NewFolderCommand)
                || WindowShortcut.TryExecute(e, new RemoteKey("F2"), RemoteKeyModifiers.None, viewModel.RenameCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Delete, RemoteKeyModifiers.None, viewModel.DeleteCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('C'), RemoteKeyModifiers.Control, viewModel.CopyCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('X'), RemoteKeyModifiers.Control, viewModel.CutCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('V'), RemoteKeyModifiers.Control, viewModel.PasteCommand);
        };

        // 窗口打开后异步加载根；内部路由指定位置时直接导航到该目录。
        _ = OpenInitialLocationAsync(viewModel, initialPath);
    }

    private static async Task OpenInitialLocationAsync(ExplorerViewModel viewModel, string? initialPath)
    {
        await viewModel.LoadRootAsync();
        await viewModel.NavigateToAsync(string.IsNullOrWhiteSpace(initialPath) ? null : initialPath);
    }

    private static string? QueryValue(Uri uri, string key) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .Where(pair => pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase))
        .Select(pair => Uri.UnescapeDataString(pair[1]))
        .FirstOrDefault();

    /// <summary>将对话框回调注入 VM：文本输入 / 确认 / 本地文件选择（上传/下载） / 消息 / 关闭。</summary>
    private static void WireDialogs(AppContext context, ExplorerViewModel vm, IExplorerClient client)
    {
        vm.RequestTextInputAsync = async (title, prompt, defaultValue, confirmLabel) =>
        {
            string? result = null;
            var tcs = new TaskCompletionSource<string?>();
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = FindOwnerWindow(context, vm);
                if (owner is null) { tcs.TrySetResult(null); return; }
                await context.ShowDialogAsync<string?>(owner, title, dialog =>
                {
                    var dvm = new TextInputDialogViewModel(prompt, defaultValue, r =>
                    {
                        result = r;
                        dialog.Close(r);
                    }, confirmLabel);
                    return new TextInputDialogView { DataContext = dvm };
                });
                tcs.TrySetResult(result);
            });
            return await tcs.Task;
        };

        vm.RequestConfirmAsync = async (title, message, confirmLabel) =>
        {
            var result = false;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = FindOwnerWindow(context, vm);
                if (owner is null) return;
                await context.ShowDialogAsync<bool?>(owner, title, dialog =>
                {
                    var dvm = new ConfirmDialogViewModel(message, r =>
                    {
                        result = r;
                        dialog.Close(r);
                    }, confirmLabel);
                    return new ConfirmDialogView { DataContext = dvm };
                });
            });
            return result;
        };

        vm.ShowMessageAsync = async (title, message) =>
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = FindOwnerWindow(context, vm);
                if (owner is null) return;
                await context.ShowDialogAsync<bool?>(owner, title, dialog =>
                {
                    var dvm = new ConfirmDialogViewModel(message, _ => dialog.Close(true), LocalizedText.Get("common.ok"));
                    return new ConfirmDialogView { DataContext = dvm };
                });
            });
        };

        vm.RequestLocalUploadFilesAsync = async () =>
        {
            var topLevel = GetTopLevel(context, vm);
            if (topLevel is null) return [];
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("explorer.select_upload_file"),
                AllowMultiple = true,
            });
            return files.Select(file => file.TryGetLocalPath())
                .OfType<string>().Select(path => new Models.LocalUploadSource(path)).ToArray();
        };

        vm.RequestLocalUploadFoldersAsync = async () =>
        {
            var topLevel = GetTopLevel(context, vm);
            if (topLevel is null) return [];
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizedText.Get("explorer.select_upload_folder"),
                AllowMultiple = true,
            });
            return folders.Select(folder => folder.TryGetLocalPath())
                .OfType<string>().Select(path => new Models.LocalUploadSource(path)).ToArray();
        };

        vm.RequestClipboardUploadSourcesAsync = async () =>
        {
            var topLevel = GetTopLevel(context, vm);
            if (topLevel?.Clipboard is null) return [];
            var transfer = await topLevel.Clipboard.TryGetDataAsync();
            if (transfer is null) return [];
            try
            {
                var items = await transfer.TryGetFilesAsync();
                return items?.Select(item => item.TryGetLocalPath()).OfType<string>()
                    .Select(path => new Models.LocalUploadSource(path)).ToArray()
                    ?? [];
            }
            finally
            {
                if (transfer is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
                else (transfer as IDisposable)?.Dispose();
            }
        };

        vm.RequestLocalSaveFileAsync = async defaultName =>
        {
            var topLevel = GetTopLevel(context, vm);
            if (topLevel is null) return null;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizedText.Get("explorer.save_download_file"),
                SuggestedFileName = defaultName,
            });
            return file?.TryGetLocalPath();
        };

        vm.RequestFileElevationAsync = async (path, capability) =>
        {
            try
            {
                await client.ElevateFileAccessAsync(path, capability);
                return true;
            }
            catch (RemoteOsAuthException ex) when (ex.Type.EndsWith("/elevation-password-required", StringComparison.Ordinal))
            {
                var password = await context.WindowManager.ShowSystemDialogAsync<string?>("管理员认证", dialog =>
                {
                    var input = new TextBox { PasswordChar = '•', PlaceholderText = "请输入当前管理员密码" };
                    var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
                    cancel.Click += (_, _) => dialog.Cancel();
                    var confirm = new Button { Content = LocalizedText.Get("common.ok"), Classes = { "primary" } };
                    confirm.Click += (_, _) => dialog.Close(input.Text);
                    return new StackPanel
                    {
                        Margin = new Thickness(20), Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = "此文件需要管理员权限才能打开。", TextWrapping = TextWrapping.Wrap },
                            input,
                            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, confirm } },
                        },
                    };
                }, new Size(420, 180));
                if (password is null) return false;
                try
                {
                    await client.ElevateFileAccessAsync(path, capability, password);
                    return true;
                }
                catch (RemoteOsAuthException retry) when (retry.Type.EndsWith("/elevation-password-invalid", StringComparison.Ordinal))
                {
                    await (vm.ShowMessageAsync?.Invoke("管理员认证", "密码不正确。") ?? Task.CompletedTask);
                    return false;
                }
            }
        };

        vm.RequestFileOperationElevationAsync = async (paths, capability) =>
        {
            try
            {
                await client.ElevateFileOperationAsync(paths, capability);
                return true;
            }
            catch (RemoteOsAuthException ex) when (ex.Type.EndsWith("/elevation-password-required", StringComparison.Ordinal))
            {
                var password = await context.WindowManager.ShowSystemDialogAsync<string?>("管理员认证", dialog =>
                {
                    var input = new TextBox { PasswordChar = '•', PlaceholderText = "请输入当前管理员密码" };
                    var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
                    cancel.Click += (_, _) => dialog.Cancel();
                    var confirm = new Button { Content = LocalizedText.Get("common.ok"), Classes = { "primary" } };
                    confirm.Click += (_, _) => dialog.Close(input.Text);
                    return new StackPanel
                    {
                        Margin = new Thickness(20), Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = "此操作需要管理员权限才能继续。授权将在当前会话中保留 5 分钟。", TextWrapping = TextWrapping.Wrap },
                            input,
                            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, confirm } },
                        },
                    };
                }, new Size(420, 190));
                if (password is null) return false;
                try
                {
                    await client.ElevateFileOperationAsync(paths, capability, password);
                    return true;
                }
                catch (RemoteOsAuthException retry) when (retry.Type.EndsWith("/elevation-password-invalid", StringComparison.Ordinal))
                {
                    await (vm.ShowMessageAsync?.Invoke("管理员认证", "密码不正确，未执行该操作。") ?? Task.CompletedTask);
                    return false;
                }
            }
        };

        var applications = context.Services.GetService(typeof(ApplicationManager)) as ApplicationManager;
        var defaults = context.Services.GetService(typeof(DefaultAppRegistry)) as DefaultAppRegistry;
        var textSniffer = context.Services.GetService(typeof(ITextFileSniffer)) as ITextFileSniffer;
        vm.OpenTerminalAtPathAsync = path =>
        {
            if (applications?.OpenTerminal(path) == true)
                return Task.CompletedTask;
            return vm.ShowMessageAsync?.Invoke(LocalizedText.Get("explorer.open_terminal"),
                LocalizedText.Get("explorer.terminal_unavailable")) ?? Task.CompletedTask;
        };
        vm.OpenFileAsync = async entry =>
        {
            var extension = Path.GetExtension(entry.Name);
            var defaultApplicationId = string.IsNullOrEmpty(extension) ? null : defaults?.Resolve(extension);
            var applicationId = defaultApplicationId is not null && applications?.SupportsFile(new AppId(defaultApplicationId), entry.Path) == true
                ? defaultApplicationId
                : applications?.FileOpenersForPath(entry.Path).FirstOrDefault()?.Id.Value;
            AppActivationResult result;
            if (applicationId is not null)
            {
                result = context.Activations.Activate(RemoteOsActivationUris.OpenFile(new AppId(applicationId), entry.Path));
            }
            // 用户显式绑定（设置页自由添加的未知扩展名）但应用未声明支持时：若该应用 SupportsTextFiles
            // 且文件确认为文本 → OpenFileAsText 绕过 Manifest 校验，保持绑定权威性。
            else if (defaultApplicationId is not null && entry.Type == FileSystemEntryType.File
                     && applications?.GetManifest(new AppId(defaultApplicationId)) is { SupportsTextFiles: true })
            {
                var isText = textSniffer is not null && textSniffer.IsTextByMimeType(entry.MimeType);
                if (!isText && textSniffer is not null)
                    isText = await textSniffer.IsTextFileAsync(entry.Path);
                var boundId = new AppId(defaultApplicationId);
                result = isText && applications.OpenFileAsText(boundId, entry.Path)
                    ? new AppActivationResult(AppActivationStatus.Activated, boundId)
                    : new AppActivationResult(AppActivationStatus.Unavailable, boundId);
            }
            else if (entry.Type == FileSystemEntryType.File && applications?.TextFileOpeners.Count > 0)
            {
                // 无应用显式声明支持 + 无用户绑定 → 先用服务端 MIME 快速判断；若未知再退化读字节嗅探
                var isText = textSniffer is not null && textSniffer.IsTextByMimeType(entry.MimeType);
                if (!isText && textSniffer is not null)
                    isText = await textSniffer.IsTextFileAsync(entry.Path);
                if (isText)
                {
                    var opener = applications.TextFileOpeners[0].Id;
                    result = applications.OpenFileAsText(opener, entry.Path)
                        ? new AppActivationResult(AppActivationStatus.Activated, opener)
                        : new AppActivationResult(AppActivationStatus.Unavailable, opener);
                }
                else
                    result = new AppActivationResult(AppActivationStatus.Unavailable);
            }
            else
                result = new AppActivationResult(AppActivationStatus.Unavailable);
            if (!result.Succeeded)
                await (vm.ShowMessageAsync?.Invoke(LocalizedText.Get("explorer.open_file"), LocalizedText.Get("explorer.no_file_opener")) ?? Task.CompletedTask);
        };

        vm.RequestOpenWithAsync = async entry =>
        {
            var owner = FindOwnerWindow(context, vm);
            var extension = Path.GetExtension(entry.Name);
            var openers = applications?.FileOpenersForPath(entry.Path) ?? Array.Empty<ApplicationInfo>();
            // 候选为空 + 文件条目 + 有文本编辑器 → 先 MIME 快速判断；不确定再退化嗅探字节
            if (openers.Count == 0 && entry.Type == FileSystemEntryType.File && applications?.TextFileOpeners.Count > 0)
            {
                var isText = textSniffer is not null && textSniffer.IsTextByMimeType(entry.MimeType);
                if (!isText && textSniffer is not null)
                    isText = await textSniffer.IsTextFileAsync(entry.Path);
                if (isText) openers = applications.TextFileOpeners;
            }
            if (owner is null || openers.Count == 0)
            {
                await (vm.ShowMessageAsync?.Invoke(LocalizedText.Get("explorer.open_with"), LocalizedText.Get("explorer.no_open_with_app")) ?? Task.CompletedTask);
                return;
            }
            var choice = await context.ShowDialogAsync<OpenWithChoice>(owner, LocalizedText.Get("explorer.open_with"), dialog =>
                new OpenWithDialogView
                {
                    DataContext = new OpenWithDialogViewModel(openers, extension, result =>
                    {
                        if (result is null) dialog.Cancel();
                        else dialog.Close(result);
                    }),
                });
            if (choice is null) return;

            if (choice.SetAsDefault && !string.IsNullOrWhiteSpace(extension))
                await SaveDefaultAppAsync(context, defaults, extension, choice.ApplicationId);

            var appId = new AppId(choice.ApplicationId);
            // 用户选中 SupportsTextFiles 应用但未声明该扩展名时，走 OpenFileAsText 绕过 Manifest 校验
            var isTextFallback = applications?.TextFileOpeners.Any(o => o.Id == appId) == true
                && !applications.SupportsFile(appId, entry.Path);
            var opened = isTextFallback
                ? applications?.OpenFileAsText(appId, entry.Path) == true
                : context.Activations.Activate(RemoteOsActivationUris.OpenFile(appId, entry.Path)).Succeeded;
            if (!opened)
                await (vm.ShowMessageAsync?.Invoke(LocalizedText.Get("explorer.open_file"), LocalizedText.Get("explorer.selected_app_unavailable")) ?? Task.CompletedTask);
        };

        vm.ShowPropertiesAsync = async properties =>
        {
            var owner = FindOwnerWindow(context, vm);
            if (owner is null) return;
            await context.ShowDialogAsync<bool>(owner, LocalizedText.Get("explorer.properties"), dialog => new FilePropertiesDialogView
            {
                DataContext = new FilePropertiesDialogViewModel(
                    properties,
                    unixMode => client.SetUnixPermissionsAsync(properties.Path, unixMode),
                    () => dialog.Close(true)),
            }, new RemoteOS.Core.Primitives.Size(720, 620));
        };
    }

    private static async Task SaveDefaultAppAsync(AppContext context, DefaultAppRegistry? defaults, string extension, string applicationId)
    {
        if (defaults is null) return;
        var mappings = defaults.Snapshot.Where(m => !m.Scheme.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Append(new DefaultAppMappingDto(extension, applicationId)).ToArray();
        defaults.SetMappings(mappings);

        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var settings = context.Services.GetService(typeof(ShellSettings)) as ShellSettings;
        var settingsClient = context.Services.GetService(typeof(ISettingsClient)) as ISettingsClient;
        if (session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace }
            || settings is null || settingsClient is null)
            return;

        await settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id, settings.ToPreferences(mappings));
    }

    /// <summary>查找 VM 对应的 ManagedWindow（遍历 WindowManager 已创建窗口，匹配 DataContext）。</summary>
    private static ManagedWindow? FindOwnerWindow(AppContext context, ExplorerViewModel vm)
    {
        // ExplorerMainView 的 DataContext == vm；ManagedWindow.View 是 RemoteWindow，其 Content 即此 view
        var wm = context.WindowManager as WindowManager;
        if (wm is null) return null;
        foreach (var w in wm.Windows)
        {
            if (w.View.Content is ExplorerMainView v && ReferenceEquals(v.DataContext, vm))
                return w;
        }
        return null;
    }

    /// <summary>获取宿主 Avalonia TopLevel（MainWindow）用于 StorageProvider 文件选择器。
    /// ManagedWindow/RemoteWindow 不是 TopLevel（它们是桌面外壳 Canvas 内的 TemplatedControl），
    /// 故取应用主窗口作为 StorageProvider 根。</summary>
    private static TopLevel? GetTopLevel(AppContext context, ExplorerViewModel vm)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
