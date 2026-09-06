using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Client.Apps.Explorer;
using Client.Apps.Explorer.Dialogs;
using Client.Apps.Settings;
using Client.Localization;
using Client.Services;
using Client.Services.Auth;
using Client.Services.DesktopRestore;
using Client.Services.VirtualSystemDrive;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Windows;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;
using RemoteOS.Protocol.Files;
using RemoteOS.Protocol.Workspace;

namespace Client.ViewModels.Shell;

/// <summary>
/// Root view-model for the RemoteOS desktop shell. Owns the window manager facade exposed to
/// the view, the desktop / start menu application entries, the taskbar window list and the clock.
/// </summary>
public partial class DesktopShellViewModel : ObservableObject
{
    private readonly WindowManager _windowManager;
    private readonly ApplicationManager _applications;
    private readonly ShellSettings _settings;
    private readonly LocalizationService _localization;
    private readonly Action _shutdown;
    private readonly IAuthSession _session;
    private readonly DesktopRestoreOrchestrator _desktopRestore;
    private readonly IExplorerClient _files;
    private readonly IRemoteFileClipboard _fileClipboard;
    private readonly DefaultAppRegistry _defaultApps;
    private readonly ISettingsClient _settingsClient;
    private readonly IAppActivationDiagnostics _activationDiagnostics;
    private readonly ITextFileSniffer _textSniffer;
    private readonly PreferencesSync _preferencesSync;
    private readonly DesktopWelcomePreferenceStore _desktopWelcomePreferences;
    private readonly ShortcutStore _shortcuts;
    private readonly ShortcutActivationRouter _shortcutRouter;
    private int _desktopFileLoadGeneration;

    /// <summary>打开桌面显示配置窗口的回调。由 View 层设置。</summary>
    public Func<Task>? RequestOpenDesktopDisplaySettingsAsync { get; set; }

    /// <summary>请求首次桌面配置引导弹窗的回调。由 View 层设置。</summary>
    public Func<Task<bool>>? RequestFirstTimeDesktopSetupAsync { get; set; }

    public DesktopShellViewModel(
        WindowManager windowManager,
        ApplicationManager applications,
        ShellSettings settings,
        LocalizationService localization,
        IAuthSession session,
        Action shutdown,
        DesktopRestoreOrchestrator desktopRestore,
        IExplorerClient files,
        IRemoteFileClipboard fileClipboard,
        DefaultAppRegistry defaultApps,
        ISettingsClient settingsClient,
        IAppActivationDiagnostics activationDiagnostics,
        ITextFileSniffer textSniffer,
        PreferencesSync preferencesSync,
        DesktopWelcomePreferenceStore desktopWelcomePreferences,
        ShortcutStore shortcuts,
        ShortcutActivationRouter shortcutRouter)
    {
        _windowManager = windowManager;
        _applications = applications;
        _settings = settings;
        _localization = localization;
        _session = session;
        _shutdown = shutdown;
        _desktopRestore = desktopRestore;
        _files = files;
        _fileClipboard = fileClipboard;
        _defaultApps = defaultApps;
        _settingsClient = settingsClient;
        _activationDiagnostics = activationDiagnostics;
        _textSniffer = textSniffer;
        _preferencesSync = preferencesSync;
        _desktopWelcomePreferences = desktopWelcomePreferences;
        _shortcuts = shortcuts;
        _shortcutRouter = shortcutRouter;

        _windowManager.WindowOpened += (_, _) => RefreshTaskbarGroups();
        _windowManager.WindowClosed += (_, _) => RefreshTaskbarGroups();
        _windowManager.ActiveWindowChanged += (_, _) => RefreshTaskbarGroups();
        _applications.RegistryChanged += (_, _) => Dispatcher.UIThread.Post(PopulateDesktop);
        _session.StateChanged += (_, state) => Dispatcher.UIThread.Post(() =>
        {
            if (state.State != AuthSessionState.Authenticated)
                _fileClipboard.Clear();
            PopulateDesktop();
        });
        _localization.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PopulateDesktop();
            OnPropertyChanged(nameof(ConnectionServer));
            OnPropertyChanged(nameof(ConnectionUser));
            OnPropertyChanged(nameof(ConnectionWorkspace));
        });
        _settings.DesktopDisplayChanged += (_, _) => Dispatcher.UIThread.Post(PopulateDesktop);

        StartClock();
    }

    /// <summary>首次桌面配置引导。仅在回调已就绪且需要时触发。
    /// 由 View 的 Loaded 回调调用，确保对话框基础设施（WindowManager/回调委托）已就绪。</summary>
    public async Task TryTriggerFirstTimeSetupAsync()
    {
        if (_session.State != AuthSessionState.Authenticated) return;
        await _preferencesSync.EnsureCurrentWorkspacePreferencesAsync();
        if (_session.State != AuthSessionState.Authenticated) return;
        if (_settings.HasCompletedFirstTimeSetup) return;
        if (_desktopWelcomePreferences.HasCompleted(_session.ServerUrl, _session.CurrentUser?.Username)) return;
        if (RequestFirstTimeDesktopSetupAsync is null) return;

        await RequestFirstTimeDesktopSetupAsync();
        _settings.HasCompletedFirstTimeSetup = true;
        _desktopWelcomePreferences.MarkCompleted(_session.ServerUrl, _session.CurrentUser?.Username);
        _ = SavePreferencesFireAndForgetAsync();
        Dispatcher.UIThread.Post(PopulateDesktop);
    }

    public WindowManager WindowManager => _windowManager;
    public ShellSettings Settings => _settings;
    public string ConnectionServer => _session.ServerUrl ?? T("shell.connection.not_connected", "Not connected");
    public string ConnectionUser => _session.CurrentUser?.Username ?? T("shell.connection.unknown_user", "Unknown user");
    public string ConnectionWorkspace => _session.CurrentWorkspace?.Name ?? T("shell.connection.default_workspace", "Default workspace");

    /// <summary>Called by the view after WindowManager has attached the desktop window host.</summary>
    public Task RestoreDesktopStateAsync(CancellationToken cancellationToken = default) =>
        _desktopRestore.RestoreAsync(cancellationToken);

    /// <summary>Live, application-grouped taskbar items.</summary>
    public ObservableCollection<TaskbarGroupViewModel> TaskbarGroups { get; } = new();

    public ObservableCollection<AppEntryViewModel> DesktopIcons { get; } = new();
    public ObservableCollection<ShortcutEntryViewModel> DesktopShortcuts { get; } = new();
    /// <summary>Entries from the authenticated user's remote Desktop special folder.</summary>
    public ObservableCollection<DesktopFileEntryViewModel> DesktopFiles { get; } = new();
    /// <summary>Application launchers and remote desktop files in the shared icon grid.</summary>
    public ObservableCollection<object> DesktopItems { get; } = new();
    public ObservableCollection<AppEntryViewModel> StartApps { get; } = new();

    // The shell supplies these UI callbacks. Keeping prompts and picker controls out of this
    // view-model lets the actual filesystem operations be shared by desktop context-menu items.
    public Func<string, string, string, Task<bool>>? RequestDesktopConfirmAsync { get; set; }
    public Func<IReadOnlyList<ApplicationInfo>, string, Task<OpenWithChoice?>>? RequestDesktopOpenWithAsync { get; set; }
    public Func<FilePropertiesDto, Task>? ShowDesktopPropertiesAsync { get; set; }

    [ObservableProperty] private bool _isStartOpen;
    [ObservableProperty] private bool _areDesktopIconsVisible = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskbarPreviewOpen))]
    private TaskbarGroupViewModel? _openTaskbarGroup;
    [ObservableProperty] private string _clock = string.Empty;
    [ObservableProperty] private string _dateText = string.Empty;

    /// <summary>Populate desktop + start menu from registered applications. Call after DI registration.</summary>
    public void PopulateDesktop()
    {
        var compatibleEntries = _applications.Registered
            // An app that needs a connected Linux Server must not be advertised on a Windows
            // Server desktop or Start menu. Launch still performs the same check for defense in depth.
            .Where(application => _applications.GetManifest(application.Id) is { } manifest
                && _applications.EvaluateCompatibility(manifest).IsCompatible)
            .Select(i => new AppEntryViewModel(Localize(i), _applications))
            .ToList();

        // ── Start 菜单始终显示全部兼容应用 ──
        StartApps.Clear();
        foreach (var entry in compatibleEntries)
            StartApps.Add(entry);

        // ── 桌面图标：根据桌面显示配置过滤 ──
        DesktopIcons.Clear();
        if (_settings.ShowBuiltInApps)
        {
            // 当 VisibleAppIds 为空时显示全部；否则仅显示列表中的
            var visibleSet = new HashSet<string>(_settings.VisibleAppIds, StringComparer.Ordinal);
            var filteredForDesktop = visibleSet.Count == 0
                ? compatibleEntries
                : compatibleEntries.Where(vm => visibleSet.Contains(vm.Id.Value)).ToList();

            foreach (var entry in filteredForDesktop)
                DesktopIcons.Add(entry);
        }

        RefreshDesktopItems();
        _ = RefreshDesktopShortcutsAsync();
        RefreshTaskbarGroups();
        _ = LoadDesktopFilesAsync();
    }

    private ApplicationInfo Localize(ApplicationInfo app)
    {
        var metadata = app.GetLocalizedMetadata(_localization.CurrentLanguage);
        return app with
        {
            DisplayName = T($"application.{app.Id.Value}.display_name", metadata.DisplayName),
            Description = metadata.Description is null
                ? null
                : T($"application.{app.Id.Value}.description", metadata.Description),
        };
    }

    [RelayCommand]
    private void ToggleStart() => IsStartOpen = !IsStartOpen;

    [RelayCommand]
    private void CloseStart() => IsStartOpen = false;

    [RelayCommand]
    private void Launch(AppId id)
    {
        _applications.Launch(id);
        IsStartOpen = false;
    }

    [RelayCommand]
    private void Shutdown() => _shutdown.Invoke();

    /// <summary>Refreshes application launchers and the authenticated user's desktop files.</summary>
    [RelayCommand]
    private void RefreshDesktop() => PopulateDesktop();

    [RelayCommand]
    private void OpenDesktopFolder()
    {
        if (!string.IsNullOrWhiteSpace(_desktopPath))
            _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.ExplorerPath(_desktopPath)));
    }

    [RelayCommand]
    private void SelectDesktopItem(object? item)
    {
        foreach (var app in DesktopIcons)
            app.IsDesktopSelected = ReferenceEquals(app, item);
        foreach (var file in DesktopFiles)
            file.IsDesktopSelected = ReferenceEquals(file, item);
    }

    [RelayCommand]
    private void ClearDesktopSelection() => SelectDesktopItem(null);

    [RelayCommand]
    private void OpenDesktopApp(AppEntryViewModel? app)
    {
        if (app is not null) app.LaunchCommand.Execute(null);
    }

    /// <summary>Shows an application's details through its existing Settings permission/details route.</summary>
    [RelayCommand]
    private void ShowDesktopAppDetails(AppEntryViewModel? app)
    {
        if (app is not null)
            _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.SettingsAppPermissions(app.Id)));
    }

    [RelayCommand]
    private async Task OpenDesktopEntryAsync(DesktopFileEntryViewModel? item)
    {
        if (item is null)
        {
            RecordDesktopFileMenuDiagnostic("open command ignored: entry was null.");
            return;
        }

        if (item.IsDirectory)
        {
            var folderResult = _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.ExplorerPath(item.Entry.Path)));
            RecordDesktopFileMenuDiagnostic($"folder open result: entry={item.DisplayName}, result={folderResult.Status}, target={folderResult.TargetAppId?.Value ?? "<none>"}.");
            return;
        }

        var extension = Path.GetExtension(item.Entry.Name);
        var defaultAppId = string.IsNullOrEmpty(extension) ? null : _defaultApps.Resolve(extension);
        AppId? defaultAppIdTyped = defaultAppId is null ? null : new AppId(defaultAppId);
        var opener = defaultAppId is not null && _applications.SupportsFile(new AppId(defaultAppId), item.Entry.Path)
            ? new AppId(defaultAppId)
            : _applications.FileOpenersForPath(item.Entry.Path).FirstOrDefault()?.Id;

        // 用户显式绑定（设置页自由添加的未知扩展名）但应用未声明支持时：若该应用 SupportsTextFiles
        // 且文件经 MIME/字节判定是文本，则用 OpenFileAsText 绕过 Manifest 校验——保持绑定的权威性。
        AppActivationResult? userBoundResult = null;
        if (opener is null && defaultAppIdTyped.HasValue)
        {
            var boundAppId = defaultAppIdTyped.Value;
            if (_applications.GetManifest(boundAppId) is { SupportsTextFiles: true })
            {
                var isTextBound = _textSniffer.IsTextByMimeType(item.Entry.MimeType);
                RecordDesktopFileMenuDiagnostic($"user-bound text sniff (mimeType fast path): entry={item.DisplayName}, bound={boundAppId.Value}, mime={item.Entry.MimeType ?? "<null>"}, isText={isTextBound}.");
                if (!isTextBound)
                {
                    isTextBound = await _textSniffer.IsTextFileAsync(item.Entry.Path);
                    RecordDesktopFileMenuDiagnostic($"user-bound text sniff (byte fallback): entry={item.DisplayName}, bound={boundAppId.Value}, isText={isTextBound}.");
                }
                if (isTextBound)
                {
                    userBoundResult = _applications.OpenFileAsText(boundAppId, item.Entry.Path)
                        ? new AppActivationResult(AppActivationStatus.Activated, boundAppId)
                        : new AppActivationResult(AppActivationStatus.Unavailable, boundAppId);
                }
            }
        }

        var userBoundTextFallback = userBoundResult is not null;
        RecordDesktopFileMenuDiagnostic(
            $"file open requested: entry={item.DisplayName}, extension={extension}, default={defaultAppId ?? "<none>"}, selected={opener?.Value ?? "<none>"}, userBoundTextFallback={userBoundTextFallback}.");
        if (userBoundResult is { } userBoundActivationResult)
        {
            RecordDesktopFileMenuDiagnostic($"user-bound text fallback open result: entry={item.DisplayName}, result={userBoundActivationResult.Status}, target={userBoundActivationResult.TargetAppId?.Value ?? "<none>"}.");
            return;
        }
        if (opener is not null)
        {
            var result = _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.OpenFile(opener.Value, item.Entry.Path)));
            RecordDesktopFileMenuDiagnostic($"file open result: entry={item.DisplayName}, result={result.Status}, target={result.TargetAppId?.Value ?? "<none>"}.");
            return;
        }

        // 没有任何应用显式声明支持该扩展名：先尝试服务端已返回的 MIME 快速判断，
        // 无法判断时再退化读字节嗅探；是文本就用默认文本编辑器（首个 SupportsTextFiles 应用）兜底打开。
        // 双击路径不弹"打开方式"对话框以避免打断流。
        if (_applications.TextFileOpeners.Count == 0)
        {
            RecordDesktopFileMenuDiagnostic("file open stopped: no compatible opener and no text-capable fallback registered.");
            return;
        }
        var isText = _textSniffer.IsTextByMimeType(item.Entry.MimeType);
        RecordDesktopFileMenuDiagnostic($"text sniff (mimeType fast path): entry={item.DisplayName}, mime={item.Entry.MimeType ?? "<null>"}, isText={isText}.");
        if (!isText)
        {
            isText = await _textSniffer.IsTextFileAsync(item.Entry.Path);
            RecordDesktopFileMenuDiagnostic($"text sniff (byte fallback): entry={item.DisplayName}, isText={isText}.");
        }
        if (!isText)
        {
            RecordDesktopFileMenuDiagnostic("file open stopped: no compatible opener and content sniff rejected text fallback.");
            return;
        }
        var textOpener = _applications.TextFileOpeners[0].Id;
        var textResult = _applications.OpenFileAsText(textOpener, item.Entry.Path)
            ? new AppActivationResult(AppActivationStatus.Activated, textOpener)
            : new AppActivationResult(AppActivationStatus.Unavailable, textOpener);
        RecordDesktopFileMenuDiagnostic($"text fallback open result: entry={item.DisplayName}, result={textResult.Status}, target={textResult.TargetAppId?.Value ?? "<none>"}.");
    }

    [RelayCommand]
    private async Task OpenDesktopEntryWithAsync(DesktopFileEntryViewModel? item)
    {
        if (item is null)
        {
            RecordDesktopFileMenuDiagnostic("open-with command ignored: entry was null.");
            return;
        }
        if (item.IsDirectory)
        {
            await OpenDesktopEntryAsync(item);
            return;
        }

        var openers = _applications.FileOpenersForPath(item.Entry.Path);
        RecordDesktopFileMenuDiagnostic($"open-with requested: entry={item.DisplayName}, candidates={openers.Count}, dialogAvailable={RequestDesktopOpenWithAsync is not null}.");

        // 没有任何应用显式声明支持时：先 MIME 快速判断，无法判断再退化嗅探字节；
        // 是文本就把 SupportsTextFiles 应用（Notepad/CodeEditor）作为 fallback 候选加入"打开方式"列表。
        if (openers.Count == 0 && _applications.TextFileOpeners.Count > 0)
        {
            var isText = _textSniffer.IsTextByMimeType(item.Entry.MimeType);
            RecordDesktopFileMenuDiagnostic($"open-with text sniff (mimeType fast path): entry={item.DisplayName}, mime={item.Entry.MimeType ?? "<null>"}, isText={isText}.");
            if (!isText)
            {
                isText = await _textSniffer.IsTextFileAsync(item.Entry.Path);
                RecordDesktopFileMenuDiagnostic($"open-with text sniff (byte fallback): entry={item.DisplayName}, isText={isText}.");
            }
            if (isText)
                openers = _applications.TextFileOpeners;
        }

        if (openers.Count == 0 || RequestDesktopOpenWithAsync is null)
        {
            RecordDesktopFileMenuDiagnostic("open-with stopped: no compatible opener or no dialog callback.");
            return;
        }

        var choice = await RequestDesktopOpenWithAsync(openers, Path.GetExtension(item.Entry.Name));
        if (choice is null)
        {
            RecordDesktopFileMenuDiagnostic("open-with cancelled.");
            return;
        }

        var selectedApplication = choice.ApplicationId;
        var extension = Path.GetExtension(item.Entry.Name);
        if (choice.SetAsDefault && !string.IsNullOrWhiteSpace(extension))
            await SaveDesktopDefaultAppAsync(extension, selectedApplication);

        // 用户选中的可能是 SupportsTextFiles 应用但未声明该扩展名（如 .enabled → Notepad）。
        // 若走 remoteos://file/open 路由会被 ApplicationManager.OpenFile 的 SupportsFile 校验拦截，
        // 这里分流：选中的应用是 TextFileOpeners 之一时用 OpenFileAsText 绕过校验。
        var appId = new AppId(selectedApplication);
        var isTextFallbackChoice = _applications.TextFileOpeners.Any(o => o.Id == appId)
            && !_applications.SupportsFile(appId, item.Entry.Path);
        var result = isTextFallbackChoice
            ? (_applications.OpenFileAsText(appId, item.Entry.Path)
                ? new AppActivationResult(AppActivationStatus.Activated, appId)
                : new AppActivationResult(AppActivationStatus.Unavailable, appId))
            : _applications.Activate(new AppActivationRequest(
                RemoteOsActivationUris.OpenFile(appId, item.Entry.Path)));
        RecordDesktopFileMenuDiagnostic($"open-with result: entry={item.DisplayName}, app={selectedApplication}, textFallback={isTextFallbackChoice}, result={result.Status}.");
    }

    [RelayCommand]
    private void CopyDesktopEntry(DesktopFileEntryViewModel? item)
    {
        if (item is null) return;
        _fileClipboard.Set([item.Entry], RemoteFileClipboardOperation.Copy);
        RecordDesktopFileMenuDiagnostic($"copy stored in desktop clipboard: entry={item.DisplayName}.");
    }

    [RelayCommand]
    private void CutDesktopEntry(DesktopFileEntryViewModel? item)
    {
        if (item is null) return;
        _fileClipboard.Set([item.Entry], RemoteFileClipboardOperation.Cut);
        RecordDesktopFileMenuDiagnostic($"cut stored in desktop clipboard: entry={item.DisplayName}.");
    }

    [RelayCommand]
    private Task PasteDesktopAsync() => PasteDesktopEntryAsync(null);

    private async Task PasteDesktopEntryAsync(DesktopFileEntryViewModel? item)
    {
        var targetDirectory = item is { IsDirectory: true } ? item.Entry.Path : _desktopPath;
        if (string.IsNullOrWhiteSpace(targetDirectory) || !_fileClipboard.HasEntries)
        {
            RecordDesktopFileMenuDiagnostic($"desktop paste stopped: targetAvailable={!string.IsNullOrWhiteSpace(targetDirectory)}, clipboardItems={_fileClipboard.Entries.Count}.");
            return;
        }

        RecordDesktopFileMenuDiagnostic($"desktop paste started: clipboardItems={_fileClipboard.Entries.Count}, operation={_fileClipboard.Operation}.");

        try
        {
            foreach (var entry in _fileClipboard.Entries)
            {
                var destination = CombineRemotePath(targetDirectory, entry.Name);
                if (PathEquals(entry.Path, destination))
                {
                    // Copying to the same Desktop should behave like a desktop file manager,
                    // producing a sibling copy instead of silently doing nothing.
                    if (_fileClipboard.Operation == RemoteFileClipboardOperation.Cut)
                        continue;
                    destination = await GetAvailableDesktopCopyPathAsync(targetDirectory, entry.Name);
                }
                if (_fileClipboard.Operation == RemoteFileClipboardOperation.Cut)
                    await _files.MoveAsync(entry.Path, destination, overwrite: false);
                else
                    await _files.CopyAsync(entry.Path, destination, overwrite: false);
            }

            if (_fileClipboard.Operation == RemoteFileClipboardOperation.Cut)
                _fileClipboard.Clear();
            RefreshDesktop();
            RecordDesktopFileMenuDiagnostic("desktop paste completed and refresh requested.");
        }
        catch (Exception ex)
        {
            RecordDesktopFileMenuDiagnostic($"desktop paste failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteDesktopEntryAsync(DesktopFileEntryViewModel? item)
    {
        if (item is null || RequestDesktopConfirmAsync is null)
        {
            RecordDesktopFileMenuDiagnostic($"delete stopped: entryAvailable={item is not null}, dialogAvailable={RequestDesktopConfirmAsync is not null}.");
            return;
        }
        var confirmed = await RequestDesktopConfirmAsync(
            T("common.delete", "Delete"),
            LocalizedText.Format(item.IsDirectory
                ? "explorer.delete_confirmation"
                : "explorer.delete_file_confirmation", item.Entry.Name),
            T("common.delete", "Delete"));
        if (!confirmed)
        {
            RecordDesktopFileMenuDiagnostic($"delete cancelled: entry={item.DisplayName}.");
            return;
        }

        try
        {
            await _files.DeleteAsync(item.Entry.Path);
            RefreshDesktop();
            RecordDesktopFileMenuDiagnostic($"delete completed: entry={item.DisplayName}; refresh requested.");
        }
        catch (Exception ex)
        {
            RecordDesktopFileMenuDiagnostic($"delete failed: entry={item.DisplayName}, error={ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ShowDesktopEntryPropertiesAsync(DesktopFileEntryViewModel? item)
    {
        if (item is null || ShowDesktopPropertiesAsync is null)
        {
            RecordDesktopFileMenuDiagnostic($"properties stopped: entryAvailable={item is not null}, dialogAvailable={ShowDesktopPropertiesAsync is not null}.");
            return;
        }
        RecordDesktopFileMenuDiagnostic($"properties requested: entry={item.DisplayName}.");
        try
        {
            var properties = await _files.GetPropertiesAsync(item.Entry.Path);
            if (properties is not null)
            {
                RecordDesktopFileMenuDiagnostic("properties loaded; showing dialog.");
                await ShowDesktopPropertiesAsync(properties);
            }
            else
                RecordDesktopFileMenuDiagnostic("properties stopped: entry no longer exists.");
        }
        catch (Exception ex)
        {
            RecordDesktopFileMenuDiagnostic($"properties failed: entry={item.DisplayName}, error={ex.GetType().Name}: {ex.Message}");
        }
    }

    public void RecordDesktopFileMenuDiagnostic(string message) =>
        _activationDiagnostics.Record($"Desktop file context: {message}");

    /// <summary>Used by the desktop-owned properties window to persist POSIX permission edits.</summary>
    public Task<FilePropertiesDto> SetDesktopUnixPermissionsAsync(string path, int unixMode) =>
        _files.SetUnixPermissionsAsync(path, unixMode);

    [RelayCommand]
    private void ShowDesktopEntryInExplorer(DesktopFileEntryViewModel? item)
    {
        if (item is null) return;
        var path = item.IsDirectory ? item.Entry.Path : Path.GetDirectoryName(item.Entry.Path);
        if (!string.IsNullOrWhiteSpace(path))
            _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.ExplorerPath(path)));
    }

    [RelayCommand]
    private void OpenFileExplorer() => LaunchApplication("remoteos.explorer");

    [RelayCommand]
    private void OpenTerminal() => LaunchApplication("remoteos.terminal");

    [RelayCommand]
    private void OpenSettings() => LaunchApplication("remoteos.settings");

    /// <summary>Opens Settings directly on its Personalization page.</summary>
    [RelayCommand]
    private void OpenPersonalization() =>
        _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.SettingsPersonalization));

    [RelayCommand]
    private void OpenTaskManager() => LaunchApplication("remoteos.taskmanager");

    [RelayCommand]
    private void ShowDesktop()
    {
        foreach (var window in _windowManager.Windows.Where(window => window.State != WindowState.Minimized).ToList())
            _windowManager.Minimize(window);

        IsStartOpen = false;
        OpenTaskbarGroup = null;
    }

    /// <summary>
    /// A single-window group keeps the familiar taskbar toggle behavior. A multi-window
    /// group opens its preview strip so the user can choose the exact window to activate or close.
    /// </summary>
    [RelayCommand]
    private void ToggleTaskbarGroup(TaskbarGroupViewModel group)
    {
        IsStartOpen = false;

        if (group.HasMultipleWindows)
        {
            OpenTaskbarGroup = ReferenceEquals(OpenTaskbarGroup, group) ? null : group;
            return;
        }

        if (group.Windows.FirstOrDefault() is { } window)
            ToggleSingleTaskbarWindow(window);
    }

    private void ToggleSingleTaskbarWindow(ManagedWindow window)
    {
        if (window.State == WindowState.Minimized)
            _windowManager.Restore(window);
        else if (window.IsActive)
            _windowManager.Minimize(window);
        else
            _windowManager.Focus(window);
    }

    [RelayCommand]
    private void ActivateTaskbarWindow(ManagedWindow window)
    {
        if (window.State == WindowState.Minimized)
            _windowManager.Restore(window);
        else
            _windowManager.Focus(window);
        OpenTaskbarGroup = null;
    }

    [RelayCommand]
    private void CloseTaskbarWindow(ManagedWindow window)
        => _windowManager.Close(window);

    public bool IsTaskbarPreviewOpen => OpenTaskbarGroup is not null;

    [RelayCommand]
    private void CloseTaskbarPreview() => OpenTaskbarGroup = null;

    private void LaunchApplication(string id)
    {
        _applications.Launch(new AppId(id));
        IsStartOpen = false;
        OpenTaskbarGroup = null;
    }

    private string? _desktopPath;

    private async Task LoadDesktopFilesAsync()
    {
        var generation = ++_desktopFileLoadGeneration;
        if (_session.State != AuthSessionState.Authenticated)
        {
            _desktopPath = null;
            DesktopFiles.Clear();
            RefreshDesktopItems();
            return;
        }

        try
        {
            var locations = await _files.GetSpecialLocationsAsync();
            var desktop = locations.FirstOrDefault(location => location.Kind == SpecialFolderKind.Desktop);
            if (generation != _desktopFileLoadGeneration) return;

            _desktopPath = desktop?.Path;
            if (desktop is null)
            {
                DesktopFiles.Clear();
                RefreshDesktopItems();
                return;
            }

            var directory = await _files.GetDirectoryAsync(desktop.Path);
            if (generation != _desktopFileLoadGeneration) return;

            var showFiles = _settings.ShowServerDesktopFiles;
            var showShortcuts = _settings.ShowServerDesktopShortcuts;

            var entries = directory.Directories
                .Concat(directory.Files.Select(file => new FileSystemEntryDto(
                    file.Path, file.Name, file.Size, FileSystemEntryType.File, file.Created, file.Modified,
                    file.Accessed, file.IsHidden, file.IsSystem, file.MimeType)))
                .Where(entry => !entry.IsHidden)
                // ── 根据桌面显示配置过滤服务器桌面文件 ──
                .Where(entry =>
                {
                    var isShortcut = entry.Type == FileSystemEntryType.File
                                     && ShellSettings.IsShortcutFile(entry.Name);
                    if (isShortcut) return showShortcuts;
                    return showFiles;
                })
                .OrderByDescending(entry => entry.Type is FileSystemEntryType.Directory or FileSystemEntryType.Drive)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => new DesktopFileEntryViewModel(entry))
                .ToList();

            DesktopFiles.Clear();
            foreach (var entry in entries) DesktopFiles.Add(entry);
            RefreshDesktopItems();
        }
        catch
        {
            // The app launcher remains usable if a server has no Desktop directory or does not
            // permit it. A later refresh retries the request without surfacing a shell-level error.
            if (generation == _desktopFileLoadGeneration)
            {
                _desktopPath = null;
                DesktopFiles.Clear();
                RefreshDesktopItems();
            }
        }
    }

    // ── 桌面显示配置相关命令 ──

    /// <summary>打开"配置桌面显示项目"窗口。</summary>
    [RelayCommand]
    private async Task OpenDesktopDisplaySettingsAsync()
    {
        if (RequestOpenDesktopDisplaySettingsAsync is not null)
            await RequestOpenDesktopDisplaySettingsAsync();
    }

    /// <summary>保存桌面显示配置到服务端（fire-and-forget，忽略瞬时错误）。</summary>
    public async Task SavePreferencesFireAndForgetAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
            return;
        try
        {
            await _settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id,
                _settings.ToPreferences(_defaultApps.Snapshot));
        }
        catch
        {
            // 保存失败不阻塞用户操作，下次同步时回退
        }
    }

    private void RefreshDesktopItems()
    {
        DesktopItems.Clear();
        foreach (var app in DesktopIcons) DesktopItems.Add(app);
        foreach (var shortcut in DesktopShortcuts) DesktopItems.Add(shortcut);
        foreach (var file in DesktopFiles) DesktopItems.Add(file);
    }

    private async Task RefreshDesktopShortcutsAsync()
    {
        var shortcuts = await _shortcuts.ListAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DesktopShortcuts.Clear();
            foreach (var shortcut in shortcuts)
                DesktopShortcuts.Add(new ShortcutEntryViewModel(shortcut, _shortcutRouter));
            RefreshDesktopItems();
        });
    }

    private static string CombineRemotePath(string directory, string name)
    {
        var separator = directory.Contains('\\') ? '\\' : '/';
        var trimmed = directory.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? separator + name : trimmed + separator + name;
    }

    private static bool PathEquals(string first, string second) => string.Equals(
        first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private async Task<string> GetAvailableDesktopCopyPathAsync(string directory, string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 1; ; index++)
        {
            var candidateName = $"{baseName} ({index}){extension}";
            var candidatePath = CombineRemotePath(directory, candidateName);
            if (await _files.GetInfoAsync(candidatePath) is null)
                return candidatePath;
        }
    }

    private async Task SaveDesktopDefaultAppAsync(string extension, string applicationId)
    {
        var mappings = _defaultApps.Snapshot
            .Where(mapping => !mapping.Scheme.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Append(new DefaultAppMappingDto(extension, applicationId))
            .ToArray();
        _defaultApps.SetMappings(mappings);

        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
            return;
        await _settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id, _settings.ToPreferences(mappings));
    }

    private void RefreshTaskbarGroups()
    {
        var groupedWindows = _windowManager.Windows
            // Modal dialogs belong to their owner. Showing them here lets taskbar activation
            // select the blocked owner, which breaks the modal focus contract.
            .Where(window => !window.IsModalDialog)
            .GroupBy(window => window.Info.OwnerAppId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var group in TaskbarGroups.ToList())
        {
            if (groupedWindows.Remove(group.AppId, out var windows))
            {
                group.Update(windows);
                if (ReferenceEquals(OpenTaskbarGroup, group) && !group.HasMultipleWindows)
                    OpenTaskbarGroup = null;
            }
            else
            {
                if (ReferenceEquals(OpenTaskbarGroup, group))
                    OpenTaskbarGroup = null;
                TaskbarGroups.Remove(group);
            }
        }

        foreach (var (appId, windows) in groupedWindows)
        {
            var app = _applications.Get(appId);
            var displayName = app is null
                ? windows[0].Title
                : T($"application.{app.Manifest.Id.Value}.display_name", app.Manifest.DisplayName);
            TaskbarGroups.Add(new TaskbarGroupViewModel(
                appId,
                displayName,
                windows));
        }
    }

    private void StartClock()
    {
        void Tick()
        {
            var now = DateTime.Now;
            var culture = SafeCulture(_settings.Language);
            var timeFmt = _settings.TimeFormat == "12h" ? "h:mm tt" : "HH:mm";
            Clock = now.ToString(timeFmt, culture);
            var dateFmt = string.IsNullOrWhiteSpace(_settings.DateFormat) ? "M/d ddd" : _settings.DateFormat;
            DateText = now.ToString(dateFmt, culture);
        }

        Tick();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick());
        timer.Start();

        static CultureInfo SafeCulture(string name)
        {
            try { return CultureInfo.GetCultureInfo(name); }
            catch { return CultureInfo.InvariantCulture; }
        }
    }

    private string T(string key, string englishFallback) => _localization.Get(key, englishFallback);
}
