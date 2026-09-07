using Avalonia.Controls;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.ComponentModel;
using Client.Apps.Explorer.Dialogs;
using Client.Localization;
using Client.Services.VirtualSystemDrive;
using Client.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Core.Windows;
using RemoteOS.AppSDK;
using RemoteOS.Shell;
using RemoteOS.WindowManager;
using RemoteOS.Runtime;
using RemoteOS.Protocol.Workspace;

namespace Client.Services;

/// <summary>
/// Serial, rollback-capable launcher transaction coordinator. It moves the existing window
/// visuals between surfaces; it never recreates applications or resets WindowManager truth.
/// </summary>
public sealed class ShellRuntime
{
    private readonly ShellCatalog _catalog;
    private readonly IWindowManager _windows;
    private readonly ShellSettings _settings;
    private readonly ShellPreferenceStore _preferences;
    private readonly DesktopShellOverlayService _overlays;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly ShellStateStore _state = new();
    private ContentControl? _host;
    private IDesktopShell? _active;
    private SurfaceRegistry? _activeSurfaces;
    private DesktopShellStateAdapter? _desktopState;
    private string _activeShellId = ShellApi.DefaultShellId;
    private long _switchIntentVersion;

    public ShellRuntime(ShellCatalog catalog, IWindowManager windows, ShellSettings settings, ShellPreferenceStore preferences,
        DesktopShellOverlayService overlays)
    {
        _catalog = catalog; _windows = windows; _settings = settings; _preferences = preferences; _overlays = overlays;
        _settings.ShellSelectionChanged += (_, id) => QueueSelectedShellSwitch(id);
        _catalog.Changed += (_, _) =>
        {
            if (_active is null) return;
            var requested = ShellApi.ResolveId(_settings.ShellSelection?.ShellId);
            if (requested != _activeShellId && _catalog.TryGet(requested, out var requestedShell) && requestedShell.IsAvailable)
                _ = SwitchAsync(requested, persist: false);
            else if (!_catalog.TryGet(_activeShellId, out _))
                // Keep the selected external shell intent intact: it may be rediscovered when a
                // package install completes, instead of being permanently replaced by default.
                _ = SwitchAsync(ShellApi.DefaultShellId, persist: false);
        };
    }

    public string ActiveShellId => _activeShellId;
    public event EventHandler<string>? ShellChanged;
    public event EventHandler<string>? ShellActivationFailed;

    public async Task AttachAsync(ContentControl host, DesktopShellViewModel workspace, CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(_host, host) && ReferenceEquals(_state.Snapshot, workspace) && _active is not null)
            return;
        // Shell preference synchronization is asynchronous. Selecting before it completes
        // briefly activates the default desktop and can overwrite an external-shell choice.
        await workspace.EnsureWorkspacePreferencesAsync();
        _host = host;
        _desktopState?.Dispose();
        _desktopState = new DesktopShellStateAdapter(workspace, _state, _catalog);
        _state.Publish(workspace, _desktopState.Current);
        _overlays.Configure(workspace);
        var local = await _preferences.LoadAsync();
        var requested = ShellApi.ResolveId(_settings.ShellSelection?.ShellId ?? local.ShellId);
        if (!_catalog.TryGet(requested, out var descriptor) || !descriptor.IsAvailable)
            requested = ShellApi.DefaultShellId;
        await SwitchAsync(requested, persist: false, cancellationToken);
        await workspace.TryTriggerFirstTimeSetupAsync();
        await workspace.RestoreDesktopStateAsync(cancellationToken);
    }

    public async Task<bool> SwitchAsync(string requestedId, bool persist = true, CancellationToken cancellationToken = default,
        long? intentVersion = null)
    {
        if (_host is null) return false;
        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            if (intentVersion is not null && intentVersion != Volatile.Read(ref _switchIntentVersion)) return false;
            var id = ShellApi.ResolveId(requestedId);
            if (_active is not null && id == _activeShellId) return true;
            if (!_catalog.TryCreate(id, out var candidate, out var createError) || candidate is null)
                return Fail(createError ?? "Shell is unavailable.");

            var registry = new SurfaceRegistry();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var actions = new DesktopShellActions(_state, _windows, SelectDesktopStyleAsync);
                var context = new ShellPresentationContext(_state, actions, _overlays, registry, new LocalizationSnapshot());
                await candidate.InitializeAsync(context, timeout.Token);
                if (!registry.IsComplete) throw new InvalidOperationException("Shell did not register a complete surface set.");
                // A selection made while an external package was loading must win. Without this
                // check, the older request can later commit and switch the desktop back.
                if (intentVersion is not null && intentVersion != Volatile.Read(ref _switchIntentVersion))
                {
                    await DisposeQuietly(candidate);
                    return false;
                }

                var old = _active;
                var oldView = _host.Content;
                try
                {
                    if (old is not null) await old.DeactivateAsync(timeout.Token);
                    _windows.Detach();
                    _host.Content = candidate.View;
                    _windows.Attach(registry.Surfaces!.WindowHost);
                    _windows.AttachFullScreenHost(registry.Surfaces.FullScreenWindowHost);
                    registry.Bind(_windows);
                    await candidate.ActivateAsync(timeout.Token);
                    _active = candidate; _activeSurfaces = registry; _activeShellId = candidate.Descriptor.Id;
                    // The caller may be using the built-in desktop as a temporary fallback
                    // while an external package is rediscovered. Only an explicit persisted
                    // selection is allowed to replace the user's stored shell intent.
                    var ownsCurrentIntent = intentVersion is null || intentVersion == Volatile.Read(ref _switchIntentVersion);
                    if (persist && ownsCurrentIntent && _settings.SelectedShellId != _activeShellId)
                        _settings.SelectedShellId = _activeShellId;
                    if (persist && ownsCurrentIntent)
                        await _preferences.SaveAsync(_activeShellId, candidate.Descriptor.PackageId, candidate.Descriptor.Version);
                    ShellChanged?.Invoke(this, _activeShellId);
                    if (old is not null) await DisposeQuietly(old);
                    return true;
                }
                catch
                {
                    _windows.Detach(); _host.Content = oldView;
                    if (old is not null && oldView is not null && _activeSurfaces?.Surfaces is { } oldSurfaces)
                    {
                        _windows.Attach(oldSurfaces.WindowHost);
                        _windows.AttachFullScreenHost(oldSurfaces.FullScreenWindowHost);
                        _activeSurfaces.Bind(_windows);
                        await old.ActivateAsync(CancellationToken.None);
                    }
                    await DisposeQuietly(candidate);
                    throw;
                }
            }
            catch (Exception ex)
            {
                ShellActivationFailed?.Invoke(this, $"{id}: {ex.GetType().Name}");
                return false;
            }
        }
        finally { _switchGate.Release(); }
    }

    private bool Fail(string diagnostic) { ShellActivationFailed?.Invoke(this, diagnostic); return false; }
    private static async Task DisposeQuietly(IDesktopShell shell) { try { await shell.DisposeAsync(); } catch { } }

    private void QueueSelectedShellSwitch(string id)
    {
        var intent = Interlocked.Increment(ref _switchIntentVersion);
        _ = SwitchAsync(id, persist: true, intentVersion: intent);
    }

    private Task<bool> SelectDesktopStyleAsync(string shellId)
    {
        var id = ShellApi.ResolveId(shellId);
        if (_catalog.TryGet(id, out var shell))
            _settings.ShellSelection = new ShellSelectionDto(id, shell.PackageId, shell.Version);
        else
            _settings.SelectedShellId = id;
        // The ShellSelectionChanged event above starts the versioned transition. Returning here
        // avoids a second, unversioned request that could overwrite a newer user selection.
        return Task.FromResult(true);
    }

    private sealed class SurfaceRegistry : IShellSurfaceRegistry
    {
        private IWindowManager? _windows;
        public ShellSurfaces? Surfaces { get; private set; }
        public bool IsComplete => Surfaces is not null;
        public void Register(ShellSurfaces surfaces)
        {
            if (Surfaces is not null) throw new InvalidOperationException("A shell may register surfaces only once.");
            if (surfaces.WindowHost is null || surfaces.FullScreenWindowHost is null || surfaces.ShellOverlayHost is null || surfaces.InputBackdrop is null)
                throw new InvalidOperationException("Shell surfaces cannot be null.");
            Surfaces = surfaces;
        }
        public void Bind(IWindowManager windows)
        {
            _windows = windows;
            if (Surfaces is null) return;
            void UpdateFullScreenBounds()
            {
                var b = Surfaces.FullScreenWindowHost.Bounds;
                windows.SetFullScreenHostBounds(new Rect(0, 0, b.Width, b.Height));
            }
            Surfaces.FullScreenWindowHost.SizeChanged += (_, _) => Dispatcher.UIThread.Post(UpdateFullScreenBounds);
            UpdateFullScreenBounds();
        }
        public void UpdateWorkArea(Rect workArea) => Dispatcher.UIThread.Post(() => _windows?.SetHostBounds(workArea));
        public void Clear() { Surfaces = null; _windows = null; }
    }

    private sealed class LocalizationSnapshot : ILocalizationSnapshot
    {
        private readonly LocalizationService _service = App.Services.GetRequiredService<LocalizationService>();
        private readonly Dictionary<EventHandler<ShellLanguageChangedEventArgs>, EventHandler<SystemLanguageChangedEventArgs>> _handlers = [];
        public string Language => _service.CurrentLanguage;
        public string Get(string key, string fallback) => _service.Get(key, fallback);

        public event EventHandler<ShellLanguageChangedEventArgs>? LanguageChanged
        {
            add
            {
                if (value is null) return;
                EventHandler<SystemLanguageChangedEventArgs> bridge = (_, args) =>
                    value(this, new ShellLanguageChangedEventArgs(args.PreviousLanguage, args.CurrentLanguage));
                lock (_handlers) _handlers[value] = bridge;
                _service.LanguageChanged += bridge;
            }
            remove
            {
                if (value is null) return;
                EventHandler<SystemLanguageChangedEventArgs>? bridge;
                lock (_handlers)
                {
                    if (!_handlers.Remove(value, out bridge)) return;
                }
                _service.LanguageChanged -= bridge;
            }
        }
    }
}

internal sealed class DesktopShellActions(ShellStateStore state, IWindowManager windows,
    Func<string, Task<bool>> activateDesktopStyle) : IShellActions
{
    private DesktopShellViewModel Vm => state.Snapshot as DesktopShellViewModel ?? throw new InvalidOperationException("Desktop state unavailable.");
    public Task LaunchAsync(AppId appId, CancellationToken cancellationToken = default) { Vm.LaunchCommand.Execute(appId); return Task.CompletedTask; }
    public async Task ActivateDesktopStyleAsync(string shellId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await activateDesktopStyle(shellId);
    }
    public Task OpenDesktopEntryAsync(string entryId, CancellationToken cancellationToken = default)
    {
        switch (Find(entryId))
        {
            case DesktopFileEntryViewModel file:
                Vm.OpenDesktopEntryCommand.Execute(file);
                break;
            case AppEntryViewModel app:
                app.LaunchCommand.Execute(null);
                break;
            case ShortcutEntryViewModel shortcut:
                shortcut.ActivateCommand.Execute(null);
                break;
        }
        return Task.CompletedTask;
    }
    public Task RefreshDesktopAsync(CancellationToken cancellationToken = default) { Vm.RefreshDesktopCommand.Execute(null); return Task.CompletedTask; }
    public Task PasteDesktopAsync(CancellationToken cancellationToken = default) { Vm.PasteDesktopCommand.Execute(null); return Task.CompletedTask; }
    public void ClearDesktopSelection() => Vm.ClearDesktopSelectionCommand.Execute(null);
    public void SelectDesktopEntry(string entryId) { var entry = Find(entryId); if (entry is not null) Vm.SelectDesktopItemCommand.Execute(entry); }
    public void SetDesktopIconsVisible(bool visible) => Vm.AreDesktopIconsVisible = visible;
    public void ShowDesktop() => Vm.ShowDesktopCommand.Execute(null);
    public void ToggleWindowGroup(AppId appId) { var group = Vm.TaskbarGroups.FirstOrDefault(x => x.AppId == appId); if (group is not null) Vm.ToggleTaskbarGroupCommand.Execute(group); }
    public void ActivateWindow(WindowId windowId) { var w = windows.Windows.FirstOrDefault(x => x.Info.Id == windowId); if (w is not null) windows.Focus(w); }
    public void MinimizeWindow(WindowId windowId) { var w = windows.Windows.FirstOrDefault(x => x.Info.Id == windowId); if (w is not null) windows.Minimize(w); }
    public void CloseWindow(WindowId windowId) { var w = windows.Windows.FirstOrDefault(x => x.Info.Id == windowId); if (w is not null) windows.Close(w); }
    public void OpenSettings(SettingsRoute route) => (route == SettingsRoute.Personalization ? Vm.OpenPersonalizationCommand : Vm.OpenSettingsCommand).Execute(null);
    public void OpenDesktopFolder() => Vm.OpenDesktopFolderCommand.Execute(null);
    public void OpenFileExplorer() => Vm.OpenFileExplorerCommand.Execute(null);
    public void OpenTerminal() => Vm.OpenTerminalCommand.Execute(null);
    public Task ExecuteDesktopEntryActionAsync(string entryId, DesktopEntryAction action, CancellationToken cancellationToken = default)
    {
        var entry = Find(entryId);
        if (entry is null) return Task.CompletedTask;
        if (action == DesktopEntryAction.Open) return OpenDesktopEntryAsync(entryId, cancellationToken);
        if (action == DesktopEntryAction.Properties && entry is AppEntryViewModel app)
        {
            Vm.ShowDesktopAppDetailsCommand.Execute(app);
            return Task.CompletedTask;
        }
        if (entry is not DesktopFileEntryViewModel file) return Task.CompletedTask;
        var command = action switch
        {
            DesktopEntryAction.OpenWith => Vm.OpenDesktopEntryWithCommand,
            DesktopEntryAction.Copy => Vm.CopyDesktopEntryCommand,
            DesktopEntryAction.Cut => Vm.CutDesktopEntryCommand,
            DesktopEntryAction.Paste => Vm.PasteDesktopCommand,
            DesktopEntryAction.Delete => Vm.DeleteDesktopEntryCommand,
            DesktopEntryAction.ShowInExplorer => Vm.ShowDesktopEntryInExplorerCommand,
            DesktopEntryAction.Properties => Vm.ShowDesktopEntryPropertiesCommand,
            _ => null,
        };
        command?.Execute(file);
        return Task.CompletedTask;
    }
    private object? Find(string entryId) => Vm.DesktopItems.FirstOrDefault(x => string.Equals(EntryId(x), entryId, StringComparison.Ordinal));
    private void Entry(string id, System.Windows.Input.ICommand command) { var entry = Find(id); if (entry is not null) command.Execute(entry); }
    private static string EntryId(object item) => item switch { DesktopFileEntryViewModel f => "file:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(f.Entry.Path)))[..16], AppEntryViewModel a => "app:" + a.Id.Value, ShortcutEntryViewModel s => "shortcut:" + s.DisplayName, _ => string.Empty };
}

/// <summary>
/// Keeps the public shell contract in step with the client-owned view model without leaking any
/// client UI types into external desktop packages.
/// </summary>
internal sealed class DesktopShellStateAdapter : IDisposable
{
    private readonly DesktopShellViewModel _workspace;
    private readonly ShellStateStore _state;
    private readonly IShellCatalog _catalog;
    private readonly HashSet<INotifyPropertyChanged> _desktopItems = [];
    private bool _disposed;

    public DesktopShellStateAdapter(DesktopShellViewModel workspace, ShellStateStore state, IShellCatalog catalog)
    {
        _workspace = workspace;
        _state = state;
        _catalog = catalog;
        _workspace.StartApps.CollectionChanged += OnCollectionChanged;
        _workspace.DesktopItems.CollectionChanged += OnCollectionChanged;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _workspace.Settings.PropertyChanged += OnSettingsPropertyChanged;
        _catalog.Changed += OnCatalogChanged;
        RefreshDesktopItemSubscriptions();
        Current = CreateSnapshot();
    }

    public ShellDesktopState Current { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.StartApps.CollectionChanged -= OnCollectionChanged;
        _workspace.DesktopItems.CollectionChanged -= OnCollectionChanged;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _workspace.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        _catalog.Changed -= OnCatalogChanged;
        foreach (var item in _desktopItems) item.PropertyChanged -= OnDesktopItemPropertyChanged;
        _desktopItems.Clear();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RefreshDesktopItemSubscriptions();
        Publish();
    }
    private void OnCatalogChanged(object? sender, EventArgs args) => Publish();

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DesktopShellViewModel.AreDesktopIconsVisible))
            Publish();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(ShellSettings.CurrentWallpaper))
            Publish();
    }

    private void OnDesktopItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppEntryViewModel.IsDesktopSelected)
            or nameof(DesktopFileEntryViewModel.IsDesktopSelected)
            or nameof(ShortcutEntryViewModel.IsDesktopSelected))
            Publish();
    }

    private void RefreshDesktopItemSubscriptions()
    {
        var current = _workspace.DesktopItems.OfType<INotifyPropertyChanged>().ToHashSet();
        foreach (var item in _desktopItems.Except(current).ToArray())
        {
            item.PropertyChanged -= OnDesktopItemPropertyChanged;
            _desktopItems.Remove(item);
        }
        foreach (var item in current.Except(_desktopItems))
        {
            item.PropertyChanged += OnDesktopItemPropertyChanged;
            _desktopItems.Add(item);
        }
    }

    private void Publish()
    {
        if (_disposed) return;
        Current = CreateSnapshot();
        _state.PublishDesktop(Current);
    }

    private ShellDesktopState CreateSnapshot()
    {
        var applications = _workspace.StartApps
            .Select(app => new ShellApplicationEntry(app.Id, app.DisplayName, app.IconGlyph, app.Description))
            .ToArray();
        var entries = _workspace.DesktopItems.Select(ToEntry).Where(entry => entry is not null).Cast<ShellDesktopEntry>().ToArray();
        var desktopStyles = _catalog.Available
            .Where(shell => shell.Source == ShellSourceKind.ExternalPackage && shell.IsAvailable)
            .Select(shell => new ShellDesktopStyleEntry(shell.Id, shell.DisplayName, shell.Version))
            .ToArray();
        return new ShellDesktopState(applications, entries, _workspace.AreDesktopIconsVisible, desktopStyles,
            _workspace.Settings.CurrentWallpaper);
    }

    private static ShellDesktopEntry? ToEntry(object item) => item switch
    {
        AppEntryViewModel app => new ShellDesktopEntry("app:" + app.Id.Value, app.DisplayName,
            ShellDesktopEntryKind.Application, app.IconGlyph, app.Id, app.IsDesktopSelected),
        DesktopFileEntryViewModel file => new ShellDesktopEntry("file:" + EntryHash(file.Entry.Path), file.DisplayName,
            file.IsDirectory ? ShellDesktopEntryKind.Folder : ShellDesktopEntryKind.File, file.IconGlyph, null, file.IsDesktopSelected),
        ShortcutEntryViewModel shortcut => new ShellDesktopEntry("shortcut:" + shortcut.DisplayName, shortcut.DisplayName,
            ShellDesktopEntryKind.Shortcut, shortcut.IconGlyph, null, shortcut.IsDesktopSelected),
        _ => null,
    };

    private static string EntryHash(string path) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16];
}

/// <summary>Single trusted client adapter for dialogs; packages receive only the narrow overlay contract.</summary>
public sealed class DesktopShellOverlayService : IShellOverlayService
{
    private DesktopShellViewModel? _vm;
    public void Configure(DesktopShellViewModel vm)
    {
        _vm = vm;
        vm.RequestDesktopConfirmAsync = (title, message, confirm) => Dialog<bool>(vm, title, new Size(460, 220), done => new ConfirmDialogView
        { DataContext = new ConfirmDialogViewModel(message, done, confirm) }).ContinueWith(task => task.Result == true);
        vm.RequestDesktopOpenWithAsync = (apps, extension) => Dialog<OpenWithChoice>(vm, LocalizedText.Get("explorer.open_with"), new Size(500, 360), done => new OpenWithDialogView
        { DataContext = new OpenWithDialogViewModel(apps, extension, done) });
        vm.ShowDesktopPropertiesAsync = properties => Dialog<bool>(vm, LocalizedText.Get("explorer.properties"), new Size(720, 620), done => new FilePropertiesDialogView
        { DataContext = new FilePropertiesDialogViewModel(properties, mode => vm.SetDesktopUnixPermissionsAsync(properties.Path, mode), () => done(true)) });
        var applications = App.Services.GetRequiredService<ApplicationManager>();
        vm.RequestOpenDesktopDisplaySettingsAsync = () => Dialog<bool>(vm, LocalizedText.Get("shell.desktop_display.title"), new Size(560, 520), done =>
            new Views.Shell.DesktopDisplayDialogs(vm.Settings, applications, () => vm.SavePreferencesFireAndForgetAsync(), result => done(result), false));
        vm.RequestFirstTimeDesktopSetupAsync = async () => await Dialog<bool>(vm, LocalizedText.Get("shell.desktop_display.welcome_title"), new Size(580, 560), done =>
            new Views.Shell.DesktopDisplayDialogs(vm.Settings, applications, () => vm.SavePreferencesFireAndForgetAsync(), result => done(result), true)) == true;
    }
    public Task ShowDesktopDisplaySettingsAsync(CancellationToken cancellationToken = default) { _vm?.OpenDesktopDisplaySettingsCommand.Execute(null); return Task.CompletedTask; }
    public Task<bool> ShowFirstRunDesktopSetupAsync(CancellationToken cancellationToken = default) => _vm?.RequestFirstTimeDesktopSetupAsync?.Invoke() ?? Task.FromResult(false);

    private static Task<TResult?> Dialog<TResult>(DesktopShellViewModel vm, string title, Size size, Func<Action<TResult?>, Control> content) =>
        vm.WindowManager.ShowShellDialogAsync<TResult>(title, dialog => content(result => dialog.Close(result!)), size);
}
