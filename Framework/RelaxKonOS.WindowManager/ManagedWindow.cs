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
    // Use standard Unicode symbols instead of Segoe MDL2 private-use code points.
    // The latter render as arbitrary letters/boxes when the Windows-only font is absent.
    public const string MinimizeGlyph = "\u2212";
    public const string CloseGlyph = "\u00D7";
    public const string MaximizeGlyphChar = "\u25A1";
    public const string RestoreGlyphChar = "\u2750";

    public ManagedWindow(WindowInfo info, RemoteWindow view, bool isModalDialog = false)
    {
        Info = info;
        View = view;
        IsModalDialog = isModalDialog;
        Sync();
    }

    public WindowInfo Info { get; }
    public RemoteWindow View { get; }

    /// <summary>Whether this window is a transient modal dialog rather than an application task.</summary>
    public bool IsModalDialog { get; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _iconGlyph;
    [ObservableProperty] private IImage? _iconImage;
    public bool HasIconImage => IconImage is not null;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaximizeGlyph))]
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

    public string MaximizeGlyph => State == WindowState.Maximized ? RestoreGlyphChar : MaximizeGlyphChar;

    /// <summary>Push authoritative state from <see cref="Info"/> into bindable properties.</summary>
    internal void Sync()
    {
        Title = Info.Title;
        IconGlyph = Info.IconGlyph;
        IconImage = AppIconImageLoader.Load(Info.IconPath);
        State = Info.State;
        CanMinimize = Info.CanMinimize;
        CanMaximize = Info.CanMaximize;
        CanResize = Info.CanResize;
    }

    partial void OnIconImageChanged(IImage? value) => OnPropertyChanged(nameof(HasIconImage));

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
