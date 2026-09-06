using System.Windows.Input;
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
    private bool _isQuickSettingsOpen;
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
        ToggleQuickSettingsCommand = new DelegateCommand(ToggleQuickSettings);
        CloseFlyoutsCommand = new DelegateCommand(CloseFlyouts);
        OpenSettingsCommand = new DelegateCommand(() => _context.Actions.OpenSettings(SettingsRoute.Root));
        OpenPersonalizationCommand = new DelegateCommand(() => _context.Actions.OpenSettings(SettingsRoute.Personalization));
        OpenDisplaySettingsCommand = new DelegateCommand(() => _context.Overlays.ShowDesktopDisplaySettingsAsync());
        RefreshDesktopCommand = new DelegateCommand(() => _context.Actions.RefreshDesktopAsync());
        ShowDesktopCommand = new DelegateCommand(_context.Actions.ShowDesktop);
        UpdateClock();
    }

    public ICommand ToggleStartCommand { get; }
    public ICommand ToggleQuickSettingsCommand { get; }
    public ICommand CloseFlyoutsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenPersonalizationCommand { get; }
    public ICommand OpenDisplaySettingsCommand { get; }
    public ICommand RefreshDesktopCommand { get; }
    public ICommand ShowDesktopCommand { get; }
    public bool IsStartOpen { get => _isStartOpen; private set => SetProperty(ref _isStartOpen, value); }
    public bool IsQuickSettingsOpen { get => _isQuickSettingsOpen; private set => SetProperty(ref _isQuickSettingsOpen, value); }
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
    public string About => T("start.about", "About this shell");
    public string UserName => T("start.user", "RemoteOS");
    public string Wifi => T("quick.wifi", "Wi-Fi");
    public string Bluetooth => T("quick.bluetooth", "Bluetooth");
    public string Focus => T("quick.focus", "Focus");
    public string OpenSettings => T("quick.open_settings", "Open settings");

    public void Activate() { UpdateClock(); _clockTimer.Start(); }
    public void Deactivate() { _clockTimer.Stop(); CloseFlyouts(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        _localizer.LanguageChanged -= OnLanguageChanged;
        _localizer.Dispose();
    }

    private string T(string key, string fallback) => _localizer.Get(key, fallback);
    private void ToggleStart() { IsQuickSettingsOpen = false; IsStartOpen = !IsStartOpen; }
    private void ToggleQuickSettings() { IsStartOpen = false; IsQuickSettingsOpen = !IsQuickSettingsOpen; }
    private void CloseFlyouts() { IsStartOpen = false; IsQuickSettingsOpen = false; _context.Actions.ClearDesktopSelection(); }
    private void OnClockTick(object? sender, EventArgs args) => UpdateClock();
    private void OnLanguageChanged(object? sender, EventArgs args) { UpdateClock(); NotifyAll(); }
    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = _localizer.GetCultureInfo();
        Clock = now.ToString("t", culture);
        Date = now.ToString("d", culture);
    }
}
