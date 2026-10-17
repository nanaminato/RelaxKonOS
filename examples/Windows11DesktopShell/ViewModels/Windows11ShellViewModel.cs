using System.Windows.Input;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Media;
using RelaxKonOS.Example.Windows11DesktopShell.Commands;
using RelaxKonOS.Example.Windows11DesktopShell.Services;
using RelaxKonOS.Shell;

namespace RelaxKonOS.Example.Windows11DesktopShell.ViewModels;

public sealed class Windows11ShellViewModel : ObservableObject, IDisposable
{
    private readonly ShellPresentationContext _context;
    private readonly ShellLocalizer _localizer;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _isStartOpen;
    private bool _isAllAppsOpen;
    private string _searchQuery = string.Empty;
    private bool _isQuickSettingsOpen;
    private bool _areDesktopIconsVisible = true;
    private bool _hasDesktopStyles;
    private IBrush? _wallpaper;
    private IBrush? _desktopItemLabelForeground;
    private string _clock = string.Empty;
    private string _date = string.Empty;
    private bool _disposed;

    public Windows11ShellViewModel(ShellPresentationContext context, ShellLocalizer localizer)
    {
        _context = context;
        _localizer = localizer;
        _localizer.LanguageChanged += OnLanguageChanged;
        _clockTimer.Tick += OnClockTick;
        ToggleStartCommand = new DelegateCommand(ToggleStart);
        ToggleAllAppsCommand = new DelegateCommand(ToggleAllApps);
        ToggleQuickSettingsCommand = new DelegateCommand(ToggleQuickSettings);
        CloseFlyoutsCommand = new DelegateCommand(CloseFlyouts);
        OpenSettingsCommand = new DelegateCommand(() => RunAfterClosingFlyouts(() => _context.Actions.OpenSettings(SettingsRoute.Root)));
        OpenPersonalizationCommand = new DelegateCommand(() => RunAfterClosingFlyouts(() => _context.Actions.OpenSettings(SettingsRoute.Personalization)));
        OpenDisplaySettingsCommand = new DelegateCommand(() => RunAfterClosingFlyoutsAsync(() => _context.Overlays.ShowDesktopDisplaySettingsAsync()));
        RefreshDesktopCommand = new DelegateCommand(() => RunAfterClosingFlyoutsAsync(() => _context.Actions.RefreshDesktopAsync()));
        ShowDesktopCommand = new DelegateCommand(() => RunAfterClosingFlyouts(_context.Actions.ShowDesktop));
        ToggleTaskbarApplicationCommand = new DelegateCommand<ShellTaskbarEntry>(entry =>
        {
            if (entry is null) return;
            CloseFlyouts();
            _context.Actions.ToggleWindowGroup(entry.Id);
        });
        ToggleTaskbarPinCommand = new DelegateCommand<ShellTaskbarEntry>(entry => entry is null ? Task.CompletedTask : _context.Actions.SetTaskbarPinnedAsync(entry.Id, !entry.IsPinned));
        PinDesktopEntryCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.PinToTaskbar));
        UnpinDesktopEntryCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.UnpinFromTaskbar));
        ToggleApplicationPinCommand = new DelegateCommand<ShellApplicationEntry>(entry => entry is null ? Task.CompletedTask :
            _context.Actions.SetTaskbarPinnedAsync(entry.Id, !TaskbarEntries.Any(item => item.Id == entry.Id && item.IsPinned)));
        OpenApplicationCommand = new DelegateCommand<ShellApplicationEntry>(OpenApplicationAsync);
        OpenDesktopEntryCommand = new DelegateCommand<ShellDesktopEntry>(OpenDesktopEntryAsync);
        OpenDesktopStyleCommand = new DelegateCommand<ShellDesktopStyleEntry>(OpenDesktopStyleAsync);
        ToggleDesktopIconsCommand = new DelegateCommand(() => _context.Actions.SetDesktopIconsVisible(!AreDesktopIconsVisible));
        PasteDesktopCommand = new DelegateCommand(() => _context.Actions.PasteDesktopAsync());
        OpenDesktopFolderCommand = new DelegateCommand(_context.Actions.OpenDesktopFolder);
        OpenFileExplorerCommand = new DelegateCommand(_context.Actions.OpenFileExplorer);
        OpenTerminalCommand = new DelegateCommand(_context.Actions.OpenTerminal);
        OpenDesktopEntryWithCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.OpenWith));
        CopyDesktopEntryCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.Copy));
        CutDesktopEntryCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.Cut));
        PasteDesktopEntryCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.Paste));
        DeleteDesktopEntryCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.Delete));
        ShowDesktopEntryInExplorerCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.ShowInExplorer));
        ShowDesktopEntryPropertiesCommand = new DelegateCommand<ShellDesktopEntry>(entry => ExecuteDesktopEntryActionAsync(entry, DesktopEntryAction.Properties));
        _context.State.Changed += OnDesktopStateChanged;
        ReloadDesktopState();
        UpdateClock();
    }

    public ObservableCollection<ShellApplicationEntry> Applications { get; } = new();
    public ObservableCollection<ShellApplicationEntry> FilteredApplications { get; } = new();
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value ?? string.Empty)) return;
            RefreshSearchResults();
            OnPropertyChanged(nameof(IsApplicationListOpen));
            OnPropertyChanged(nameof(IsPinnedOpen));
            OnPropertyChanged(nameof(StartSectionTitle));
        }
    }
    public ObservableCollection<ShellDesktopEntry> DesktopEntries { get; } = new();
    public ObservableCollection<ShellDesktopStyleEntry> DesktopStyleEntries { get; } = new();
    public ObservableCollection<ShellTaskbarEntry> TaskbarEntries { get; } = new();
    public ICommand ToggleTaskbarApplicationCommand { get; }
    public ICommand ToggleTaskbarPinCommand { get; }
    public ICommand PinDesktopEntryCommand { get; }
    public ICommand UnpinDesktopEntryCommand { get; }
    public ICommand ToggleStartCommand { get; }
    public ICommand ToggleAllAppsCommand { get; }
    public ICommand ToggleQuickSettingsCommand { get; }
    public ICommand CloseFlyoutsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenPersonalizationCommand { get; }
    public ICommand OpenDisplaySettingsCommand { get; }
    public ICommand RefreshDesktopCommand { get; }
    public ICommand ShowDesktopCommand { get; }
    public ICommand OpenApplicationCommand { get; }
    public ICommand ToggleApplicationPinCommand { get; }
    public ICommand OpenDesktopEntryCommand { get; }
    public ICommand OpenDesktopStyleCommand { get; }
    public ICommand ToggleDesktopIconsCommand { get; }
    public ICommand PasteDesktopCommand { get; }
    public ICommand OpenDesktopFolderCommand { get; }
    public ICommand OpenFileExplorerCommand { get; }
    public ICommand OpenTerminalCommand { get; }
    public ICommand OpenDesktopEntryWithCommand { get; }
    public ICommand CopyDesktopEntryCommand { get; }
    public ICommand CutDesktopEntryCommand { get; }
    public ICommand PasteDesktopEntryCommand { get; }
    public ICommand DeleteDesktopEntryCommand { get; }
    public ICommand ShowDesktopEntryInExplorerCommand { get; }
    public ICommand ShowDesktopEntryPropertiesCommand { get; }
    public bool IsStartOpen { get => _isStartOpen; private set => SetProperty(ref _isStartOpen, value); }
    public bool IsAllAppsOpen
    {
        get => _isAllAppsOpen;
        private set
        {
            if (!SetProperty(ref _isAllAppsOpen, value)) return;
            OnPropertyChanged(nameof(StartSectionTitle));
            OnPropertyChanged(nameof(AllAppsButtonText));
            OnPropertyChanged(nameof(IsPinnedOpen));
            OnPropertyChanged(nameof(IsApplicationListOpen));
        }
    }
    public bool IsQuickSettingsOpen { get => _isQuickSettingsOpen; private set => SetProperty(ref _isQuickSettingsOpen, value); }
    public bool AreDesktopIconsVisible { get => _areDesktopIconsVisible; private set => SetProperty(ref _areDesktopIconsVisible, value); }
    public bool HasDesktopStyles { get => _hasDesktopStyles; private set => SetProperty(ref _hasDesktopStyles, value); }
    /// <summary>Host-owned wallpaper shared by every desktop shell, including custom images.</summary>
    public IBrush? Wallpaper { get => _wallpaper; private set => SetProperty(ref _wallpaper, value); }
    /// <summary>Theme-resolved label foreground supplied by the host's built-in desktop.</summary>
    public IBrush? DesktopItemLabelForeground { get => _desktopItemLabelForeground; private set => SetProperty(ref _desktopItemLabelForeground, value); }
    public string Clock { get => _clock; private set => SetProperty(ref _clock, value); }
    public string Date { get => _date; private set => SetProperty(ref _date, value); }

    public string Start => T("taskbar.start", "Start");
    public string Search => T("taskbar.search", "Search");
    public string TaskView => T("taskbar.task_view", "Task view");
    public string Settings => T("common.settings", "Settings");
    public string Personalization => T("common.personalization", "Personalization");
    public string DisplaySettings => T("common.display_settings", "Display settings");
    public string RefreshDesktop => T("common.refresh_desktop", "Refresh desktop");
    public string ShowDesktop => T("common.show_desktop", "Show desktop");
    public string HiddenIcons => T("taskbar.hidden_icons", "Hidden icons");
    public string QuickSettings => T("taskbar.quick_settings", "Quick settings");
    public string SearchPlaceholder => T("start.search_placeholder", "Type here to search");
    public string Pinned => T("start.pinned", "Pinned");
    public string AllApps => T("start.all_apps", "All apps  ›");
    public string BackToPinned => T("start.back_to_pinned", "‹ Back");
    public string AllApplications => T("start.all_applications", "All apps");
    public string StartSectionTitle => IsApplicationListOpen ? AllApplications : Pinned;
    public string AllAppsButtonText => IsAllAppsOpen ? BackToPinned : AllApps;
    public bool IsApplicationListOpen => IsAllAppsOpen || !string.IsNullOrWhiteSpace(SearchQuery);
    public bool IsPinnedOpen => !IsApplicationListOpen;
    public string About => T("start.about", "About this shell");
    public string UserName => T("start.user", "RelaxKonOS");
    public string Wifi => T("quick.wifi", "Wi-Fi");
    public string Bluetooth => T("quick.bluetooth", "Bluetooth");
    public string Focus => T("quick.focus", "Focus");
    public string OpenSettings => T("quick.open_settings", "Open settings");
    public string DesktopStylesTitle => T("desktop.styles", "Desktop styles");
    public string View => T("common.view", "View");
    public string ShowDesktopIcons => T("desktop.context.show_icons", "Show desktop icons");
    public string Paste => T("common.paste", "Paste");
    public string ConfigureDesktopDisplay => T("desktop.context.configure_display", "Configure desktop display...");
    public string OpenDesktopFolder => T("desktop.context.open_folder", "Open desktop folder");
    public string OpenFileExplorer => T("desktop.context.open_explorer", "Open File Explorer");
    public string OpenTerminal => T("desktop.context.open_terminal", "Open Terminal");
    public string PinToTaskbar => T("taskbar.pin", "Pin to taskbar");
    public string UnpinFromTaskbar => T("taskbar.unpin", "Unpin from taskbar");
    public string Open => T("common.open", "Open");
    public string OpenWith => T("desktop.context.open_with", "Open with...");
    public string Copy => T("common.copy", "Copy");
    public string Cut => T("common.cut", "Cut");
    public string Delete => T("common.delete", "Delete");
    public string ShowInExplorer => T("desktop.context.show_in_explorer", "Show in File Explorer");
    public string Properties => T("desktop.context.properties", "Properties");
    public string AppDetails => T("desktop.context.app_details", "App details");
    public string ThisPc => T("desktop.this_pc", "This PC");
    public string Documents => T("desktop.documents", "Documents");
    public string ProjectFile => T("desktop.project_file", "Project notes.txt");
    public string RecycleBin => T("desktop.recycle_bin", "Recycle Bin");

    public void Activate()
    {
        UpdateClock();
        _clockTimer.Start();
    }
    public void Deactivate()
    {
        _clockTimer.Stop();
        CloseFlyouts();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        _localizer.LanguageChanged -= OnLanguageChanged;
        _context.State.Changed -= OnDesktopStateChanged;
        _localizer.Dispose();
    }

    private string T(string key, string fallback) => _localizer.Get(key, fallback);
    private void RunAfterClosingFlyouts(Action action)
    {
        CloseFlyouts();
        action();
    }
    private async Task RunAfterClosingFlyoutsAsync(Func<Task> action)
    {
        CloseFlyouts();
        await action();
    }
    private void ToggleStart()
    {
        IsQuickSettingsOpen = false;
        IsStartOpen = !IsStartOpen;
        if (!IsStartOpen) { IsAllAppsOpen = false; SearchQuery = string.Empty; }
    }
    private void ToggleAllApps()
    {
        IsQuickSettingsOpen = false;
        IsStartOpen = true;
        SearchQuery = string.Empty;
        IsAllAppsOpen = !IsAllAppsOpen;
    }
    private void ToggleQuickSettings() { IsStartOpen = false; IsQuickSettingsOpen = !IsQuickSettingsOpen; }
    private void CloseFlyouts() { IsStartOpen = false; IsAllAppsOpen = false; SearchQuery = string.Empty; IsQuickSettingsOpen = false; }
    private void RefreshSearchResults()
    {
        var query = SearchQuery.Trim();
        FilteredApplications.Clear();
        foreach (var app in Applications.Where(app => app.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                     || app.Id.Value.Contains(query, StringComparison.OrdinalIgnoreCase)))
            FilteredApplications.Add(app);
    }
    private void OnClockTick(object? sender, EventArgs args) => UpdateClock();
    private void OnLanguageChanged(object? sender, EventArgs args) { UpdateClock(); NotifyAll(); }
    private void OnDesktopStateChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) ReloadDesktopState();
        else Dispatcher.UIThread.Post(ReloadDesktopState);
    }
    private void ReloadDesktopState()
    {
        var state = _context.State.Desktop;
        var taskbar = state?.TaskbarEntries ?? [];
        if (!TaskbarEntries.SequenceEqual(taskbar))
        {
            TaskbarEntries.Clear();
            foreach (var entry in taskbar) TaskbarEntries.Add(entry);
        }

        if (state is null)
        {
            Applications.Clear();
            RefreshSearchResults();
            DesktopEntries.Clear();
            DesktopStyleEntries.Clear();
            AreDesktopIconsVisible = false;
            HasDesktopStyles = false;
            Wallpaper = null;
            DesktopItemLabelForeground = null;
            return;
        }
        if (!Applications.SequenceEqual(state.Applications))
        {
            Applications.Clear();
            foreach (var application in state.Applications) Applications.Add(application);
            RefreshSearchResults();
        }
        // Keep controls alive during selection changes so double-click, focus and context menus
        // survive host snapshot publication. Replace the catalog only when its contents change.
        var sameEntries = DesktopEntries.Count == state.DesktopEntries.Count &&
            DesktopEntries.Zip(state.DesktopEntries).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.DisplayName == pair.Second.DisplayName &&
                pair.First.Kind == pair.Second.Kind && pair.First.IconGlyph == pair.Second.IconGlyph &&
                pair.First.IsTaskbarPinned == pair.Second.IsTaskbarPinned &&
                ReferenceEquals(pair.First.IconImage, pair.Second.IconImage));
        if (sameEntries)
        {
            for (var index = 0; index < DesktopEntries.Count; index++)
                DesktopEntries[index].IsSelected = state.DesktopEntries[index].IsSelected;
        }
        else
        {
            DesktopEntries.Clear();
            foreach (var entry in state.DesktopEntries) DesktopEntries.Add(entry with { });
        }
        if (!DesktopStyleEntries.SequenceEqual(state.DesktopStyles ?? []))
        {
            DesktopStyleEntries.Clear();
            foreach (var desktopStyle in state.DesktopStyles ?? []) DesktopStyleEntries.Add(desktopStyle);
        }
        AreDesktopIconsVisible = state.AreDesktopIconsVisible;
        HasDesktopStyles = DesktopStyleEntries.Count > 0;
        Wallpaper = state.Wallpaper;
        DesktopItemLabelForeground = state.DesktopItemLabelForeground;
    }
    private async Task OpenApplicationAsync(ShellApplicationEntry? application)
    {
        if (application is null) return;
        CloseFlyouts();
        await _context.Actions.LaunchAsync(application.Id);
    }
    /// <summary>Matches the host desktop: a primary click selects an item, and double-click opens it.</summary>
    public ShellDesktopEntry[] SelectionFor(ShellDesktopEntry entry) => entry.IsSelected
        ? DesktopEntries.Where(item => item.IsSelected).ToArray() : [entry];
    public void SelectDesktopEntry(ShellDesktopEntry? entry)
    {
        if (entry is not null) _context.Actions.SetDesktopSelection([entry.Id]);
    }

    public async Task OpenDesktopEntryAsync(ShellDesktopEntry? entry)
    {
        if (entry is null) return;
        await _context.Actions.OpenDesktopEntryAsync(entry.Id);
        _context.Actions.ClearDesktopSelection();
    }
    public void SetDesktopSelection(IEnumerable<string> ids) => _context.Actions.SetDesktopSelection(ids.ToArray());
    public void ClearDesktopSelection() => _context.Actions.ClearDesktopSelection();

    private Task ExecuteDesktopEntryActionAsync(ShellDesktopEntry? entry, DesktopEntryAction action) =>
        entry is null ? Task.CompletedTask : _context.Actions.ExecuteDesktopEntryActionAsync(entry.Id, action);
    private async Task OpenDesktopStyleAsync(ShellDesktopStyleEntry? desktopStyle)
    {
        if (desktopStyle is null) return;
        await _context.Actions.ActivateDesktopStyleAsync(desktopStyle.Id);
    }
    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = _localizer.GetCultureInfo();
        Clock = now.ToString("t", culture);
        Date = now.ToString("d", culture);
    }
}
