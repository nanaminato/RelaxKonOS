using RelaxKonOS.AppSDK;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.VirtualSystemDrive;

namespace RelaxKonOS.Client.Services;

/// <summary>One transient banner for automation notifications; no queue or notification centre.</summary>
public sealed partial class DesktopNotificationService : ObservableObject, IAutomationNotificationSink, IDisposable
{
    private readonly DesktopDevicePreferences _preferences;
    private readonly IAuthSession _session;
    private readonly IAppActivationDiagnostics _diagnostics;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _hasNotification;
    private bool _disposed;
    private int _generation;

    public DesktopNotificationService(DesktopDevicePreferences preferences, IAuthSession session, IAppActivationDiagnostics diagnostics)
    {
        _preferences = preferences;
        _session = session;
        _diagnostics = diagnostics;
        preferences.Changed += OnPreferencesChanged;
        session.StateChanged += OnSessionChanged;
        _timer.Tick += OnTimer;
    }

    public void Notify(string title, string message)
    {
        _diagnostics.Record($"Automation notification: title={title.Length}chars, message={message.Length}chars.");
        var generation = Volatile.Read(ref _generation);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || generation != Volatile.Read(ref _generation) || _session.State != AuthSessionState.Authenticated
                || !_preferences.Value.NotificationsEnabled || _preferences.Value.DoNotDisturb) return;
            Title = title;
            Message = message;
            HasNotification = true;
            _timer.Stop(); _timer.Start();
        });
    }

    [RelayCommand]
    private void Dismiss() { _timer.Stop(); HasNotification = false; Title = ""; Message = ""; }
    private void OnTimer(object? sender, EventArgs e) => Dismiss();
    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        if (!_preferences.Value.NotificationsEnabled || _preferences.Value.DoNotDisturb)
        {
            Interlocked.Increment(ref _generation);
            Dispatcher.UIThread.Post(Dismiss);
        }
    }
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs e)
    {
        Interlocked.Increment(ref _generation);
        Dispatcher.UIThread.Post(Dismiss);
    }
    public void Dispose()
    {
        _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTimer;
        _preferences.Changed -= OnPreferencesChanged;
        _session.StateChanged -= OnSessionChanged;
    }
}
