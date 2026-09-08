using System.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Examples.VideoPlayer.Services;

/// <summary>Keeps the native VLC child window aligned with the managed RelaxKonOS window lifecycle.</summary>
public sealed class VideoPlayerWindowLifetime
{
    private readonly IExternalAppWindowHandle _window;
    private readonly LibVlcPlaybackService _playback;
    private readonly PropertyChangedEventHandler _windowChanged;

    public VideoPlayerWindowLifetime(IExternalAppWindowHandle window, LibVlcPlaybackService playback)
    {
        _window = window;
        _playback = playback;
        _windowChanged = OnWindowChanged;
        window.Window.PropertyChanged += _windowChanged;
        window.Closed.Register(Close);
        SyncSurfaceVisibility();
    }

    private void OnWindowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ManagedWindow.IsActive) or nameof(ManagedWindow.State))
            SyncSurfaceVisibility();
    }

    private void SyncSurfaceVisibility() => _playback.SetSurfaceVisible(_window.Window.IsActive && _window.Window.IsOnScreen);

    private void Close()
    {
        _window.Window.PropertyChanged -= _windowChanged;
        _ = _playback.DisposeAsync().AsTask();
    }
}
