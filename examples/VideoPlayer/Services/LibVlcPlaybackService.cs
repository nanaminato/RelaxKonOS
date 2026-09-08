using System.Reflection;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Examples.VideoPlayer.Services;

/// <summary>Owns LibVLC native resources; it contains no window or view-model policy.</summary>
public sealed class LibVlcPlaybackService : IAsyncDisposable
{
    private static int _coreInitialized;
    private VideoView? _videoView;
    private LibVLC? _libVlc;
    private Media? _media;
    private MediaPlayer? _player;
    private IExternalMediaLease? _lease;

    public bool IsPlaying => _player?.IsPlaying == true;

    public void Attach(VideoView videoView) => _videoView = videoView;

    public void Start(Uri playbackUri, IExternalMediaLease lease)
    {
        if (_videoView is null)
            throw new InvalidOperationException("A video surface must be attached before playback starts.");

        InitializeCore();
        _libVlc = new LibVLC("--no-video-title-show");
        _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
        _videoView.MediaPlayer = _player;
        _lease = lease;
        _media = new Media(_libVlc, playbackUri);
        _player.Play(_media);
    }

    public void TogglePlayPause()
    {
        if (_player is null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    public void Stop() => _player?.Stop();

    public void SetSurfaceVisible(bool visible)
    {
        if (_videoView is not null)
            _videoView.IsVisible = visible;
    }

    public async ValueTask DisposeAsync()
    {
        _player?.Stop();
        if (_videoView is not null)
            _videoView.MediaPlayer = null;
        _media?.Dispose();
        _player?.Dispose();
        _libVlc?.Dispose();
        _media = null;
        _player = null;
        _libVlc = null;
        if (_lease is not null)
            await _lease.DisposeAsync();
        _lease = null;
    }

    private static void InitializeCore()
    {
        if (Interlocked.Exchange(ref _coreInitialized, 1) != 0)
            return;
        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var nativeDirectory = Path.Combine(assemblyDirectory, "libvlc", "win-x64");
        LibVLCSharp.Shared.Core.Initialize(Directory.Exists(nativeDirectory) ? nativeDirectory : assemblyDirectory);
    }
}
