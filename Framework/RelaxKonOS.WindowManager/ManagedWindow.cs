using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Windows;

namespace RelaxKonOS.WindowManager;

/// <summary>
/// View-model / public handle for a single managed window. Bound to both the
/// <see cref="RemoteWindow"/> chrome and the taskbar entry. The <see cref="WindowManager"/>
/// owns the authoritative state in <see cref="Info"/>; this object mirrors it for binding.
/// </summary>
public partial class ManagedWindow : ObservableObject
{
    private string? _loadedIconPath;

    public ManagedWindow(WindowInfo info, RemoteWindow view, bool isModalDialog = false)
    {
        Info = info;
        View = view;
        Thumbnail = new WindowThumbnail(view);
        IsModalDialog = isModalDialog;
        Sync();
    }

    public WindowInfo Info { get; }
    public RemoteWindow View { get; }
    public WindowThumbnail Thumbnail { get; }

    /// <summary>Whether this window is a transient modal dialog rather than an application task.</summary>
    public bool IsModalDialog { get; }

    /// <summary>
    /// Optional guard consulted before a user-initiated close (title-bar button, Alt+F4, taskbar
    /// or window overview). Return <c>false</c> to keep the window open, for example to confirm
    /// discarding unsaved changes. Owner and session teardown bypass this guard through
    /// <see cref="WindowManager.ForceClose"/>.
    /// </summary>
    public Func<Task<bool>>? CanCloseAsync { get; set; }

    /// <summary>True while <see cref="CanCloseAsync"/> is running, so repeat gestures check once.</summary>
    internal bool IsCheckingClose { get; set; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _iconGlyph;
    [ObservableProperty] private IImage? _iconImage;
    public bool HasIconImage => IconImage is not null;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMaximized))]
    [NotifyPropertyChangedFor(nameof(IsOnScreen))]
    [NotifyPropertyChangedFor(nameof(IsFullScreen))]
    private WindowState _state;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _canMinimize = true;
    [ObservableProperty] private bool _canMaximize = true;
    [ObservableProperty] private bool _canResize = true;

    /// <summary>True when the window is visible on the desktop (i.e. not minimized).</summary>
    public bool IsOnScreen => State != WindowState.Minimized;

    /// <summary>True when this window currently occupies the shell-wide full-screen overlay.</summary>
    public bool IsFullScreen => State == WindowState.FullScreen;

    /// <summary>True when this window occupies the whole work area, so its caption offers "restore".</summary>
    public bool IsMaximized => State == WindowState.Maximized;

    /// <summary>Push authoritative state from <see cref="Info"/> into bindable properties.</summary>
    internal void Sync()
    {
        Title = Info.Title;
        IconGlyph = Info.IconGlyph;
        if (_loadedIconPath != Info.IconPath)
        {
            var previous = IconImage;
            IconImage = AppIconImageLoader.Load(Info.IconPath);
            _loadedIconPath = Info.IconPath;
            (previous as IDisposable)?.Dispose();
        }
        State = Info.State;
        CanMinimize = Info.CanMinimize;
        CanMaximize = Info.CanMaximize;
        CanResize = Info.CanResize;
    }

    partial void OnIconImageChanged(IImage? value) => OnPropertyChanged(nameof(HasIconImage));

    internal void ReleaseImages()
    {
        Thumbnail.Dispose();
        var previous = IconImage;
        IconImage = null;
        (previous as IDisposable)?.Dispose();
    }

    public event EventHandler? FocusRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? MaximizeToggleRequested;
    public event EventHandler? TaskbarToggleRequested;

    /// <summary>
    /// Raised when an unhandled key-down event bubbles from this active window's content.
    /// Set <see cref="RemoteKeyEventArgs.Handled"/> to prevent the key from reaching the
    /// desktop shell and client host.
    /// </summary>
    public event EventHandler<RemoteKeyEventArgs>? KeyDown;

    /// <summary>Raised when a key-up event bubbles from this active window's content.</summary>
    public event EventHandler<RemoteKeyEventArgs>? KeyUp;

    // Window-manager defaults deliberately run after public application handlers. This keeps
    // Esc application-overridable while still providing a consistent full-screen/modal fallback.
    internal event EventHandler<RemoteKeyEventArgs>? KeyDownFallback;

    internal void RaiseKeyDown(RemoteKeyEventArgs e)
    {
        KeyDown?.Invoke(this, e);
        if (!e.Handled)
            KeyDownFallback?.Invoke(this, e);
    }

    internal void RaiseKeyUp(RemoteKeyEventArgs e) => KeyUp?.Invoke(this, e);

    [RelayCommand] private void Activate() => FocusRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Minimize() => MinimizeRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ToggleMaximize() => MaximizeToggleRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void TaskbarToggle() => TaskbarToggleRequested?.Invoke(this, EventArgs.Empty);
}
