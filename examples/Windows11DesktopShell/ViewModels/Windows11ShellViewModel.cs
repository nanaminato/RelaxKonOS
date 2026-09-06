using System.Windows.Input;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Example.Windows11DesktopShell.Commands;
using Example.Windows11DesktopShell.Services;
using RemoteOS.Shell;

namespace Example.Windows11DesktopShell.ViewModels;

public sealed class Windows11ShellViewModel : ObservableObject, IDisposable
{
    private readonly ShellPresentationContext _context;
    private readonly ShellLocalizer _localizer;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _isStartOpen;
    private bool _isAllAppsOpen;
    private bool _isQuickSettingsOpen;
    private bool _areDesktopIconsVisible = true;
    private bool _hasDesktopStyles;
    private bool _isLoading = true;
    private int _loadGeneration;
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
        OpenSettingsCommand = new DelegateCommand(() => _context.Actions.OpenSettings(SettingsRoute.Root));
        OpenPersonalizationCommand = new DelegateCommand(() => _context.Actions.OpenSettings(SettingsRoute.Personalization));
        OpenDisplaySettingsCommand = new DelegateCommand(() => _context.Overlays.ShowDesktopDisplaySettingsAsync());
        RefreshDesktopCommand = new DelegateCommand(() => _context.Actions.RefreshDesktopAsync());
        ShowDesktopCommand = new DelegateCommand(_context.Actions.ShowDesktop);
        OpenApplicationCommand = new DelegateCommand<ShellApplicationEntry>(OpenApplicationAsync);
        OpenDesktopEntryCommand = new DelegateCommand<ShellDesktopEntry>(OpenDesktopEntryAsync);
        OpenDesktopStyleCommand = new DelegateCommand<ShellDesktopStyleEntry>(OpenDesktopStyleAsync);
        _context.State.Changed += OnDesktopStateChanged;
        ReloadDesktopState();
        UpdateClock();
    }

    public ObservableCollection<ShellApplicationEntry> Applications { get; } = new();
    public ObservableCollection<ShellDesktopEntry> DesktopEntries { get; } = new();
    public ObservableCollection<ShellDesktopStyleEntry> DesktopStyleEntries { get; } = new();
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
    public ICommand OpenDesktopEntryCommand { get; }
    public ICommand OpenDesktopStyleCommand { get; }
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
        }
    }
    public bool IsQuickSettingsOpen { get => _isQuickSettingsOpen; private set => SetProperty(ref _isQuickSettingsOpen, value); }
    public bool AreDesktopIconsVisible { get => _areDesktopIconsVisible; private set => SetProperty(ref _areDesktopIconsVisible, value); }
    public bool HasDesktopStyles { get => _hasDesktopStyles; private set => SetProperty(ref _hasDesktopStyles, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
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
    public string StartSectionTitle => IsAllAppsOpen ? AllApplications : Pinned;
    public string AllAppsButtonText => IsAllAppsOpen ? BackToPinned : AllApps;
    public bool IsPinnedOpen => !IsAllAppsOpen;
    public string About => T("start.about", "About this shell");
    public string UserName => T("start.user", "RemoteOS");
    public string Wifi => T("quick.wifi", "Wi-Fi");
    public string Bluetooth => T("quick.bluetooth", "Bluetooth");
    public string Focus => T("quick.focus", "Focus");
    public string OpenSettings => T("quick.open_settings", "Open settings");
    public string LoadingTitle => T("loading.title", "Getting your desktop ready");
    public string LoadingDescription => T("loading.description", "Loading applications and desktop items…");
    public string DesktopStylesTitle => T("desktop.styles", "Desktop styles");
    public string ThisPc => T("desktop.this_pc", "This PC");
    public string Documents => T("desktop.documents", "Documents");
    public string ProjectFile => T("desktop.project_file", "Project notes.txt");
    public string RecycleBin => T("desktop.recycle_bin", "Recycle Bin");

    public void Activate()
    {
        UpdateClock();
        _clockTimer.Start();
        ShowLoadingTransition();
    }
    public void Deactivate()
    {
        _loadGeneration++;
        IsLoading = true;
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
    private void ToggleStart()
    {
        IsQuickSettingsOpen = false;
        IsStartOpen = !IsStartOpen;
        if (!IsStartOpen) IsAllAppsOpen = false;
    }
    private void ToggleAllApps()
    {
        IsQuickSettingsOpen = false;
        IsStartOpen = true;
        IsAllAppsOpen = !IsAllAppsOpen;
    }
    private void ToggleQuickSettings() { IsStartOpen = false; IsQuickSettingsOpen = !IsQuickSettingsOpen; }
    private void CloseFlyouts() { IsStartOpen = false; IsAllAppsOpen = false; IsQuickSettingsOpen = false; _context.Actions.ClearDesktopSelection(); }
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
        Applications.Clear();
        DesktopEntries.Clear();
        DesktopStyleEntries.Clear();
        if (state is null)
        {
            AreDesktopIconsVisible = false;
            HasDesktopStyles = false;
            return;
        }
        foreach (var application in state.Applications) Applications.Add(application);
        foreach (var entry in state.DesktopEntries) DesktopEntries.Add(entry);
        foreach (var desktopStyle in state.DesktopStyles ?? []) DesktopStyleEntries.Add(desktopStyle);
        AreDesktopIconsVisible = state.AreDesktopIconsVisible;
        HasDesktopStyles = DesktopStyleEntries.Count > 0;
    }
    private async Task OpenApplicationAsync(ShellApplicationEntry? application)
    {
        if (application is null) return;
        await _context.Actions.LaunchAsync(application.Id);
        CloseFlyouts();
    }
    /// <summary>Matches the host desktop: a primary click selects an item, and double-click opens it.</summary>
    public void SelectDesktopEntry(ShellDesktopEntry? entry)
    {
        if (entry is not null) _context.Actions.SelectDesktopEntry(entry.Id);
    }

    public async Task OpenDesktopEntryAsync(ShellDesktopEntry? entry)
    {
        if (entry is null) return;
        await _context.Actions.OpenDesktopEntryAsync(entry.Id);
        _context.Actions.ClearDesktopSelection();
    }
    private async Task OpenDesktopStyleAsync(ShellDesktopStyleEntry? desktopStyle)
    {
        if (desktopStyle is null) return;
        await _context.Actions.ActivateDesktopStyleAsync(desktopStyle.Id);
    }
    private void ShowLoadingTransition()
    {
        IsLoading = true;
        var generation = ++_loadGeneration;
        DispatcherTimer.RunOnce(() =>
        {
            if (!_disposed && generation == _loadGeneration) IsLoading = false;
        }, TimeSpan.FromMilliseconds(420));
    }
    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = _localizer.GetCultureInfo();
        Clock = now.ToString("t", culture);
        Date = now.ToString("d", culture);
    }
}
