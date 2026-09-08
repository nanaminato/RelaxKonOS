using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Examples.VideoPlayer.Services;

namespace RelaxKonOS.Examples.VideoPlayer.ViewModels;

/// <summary>Coordinates the permission-gated media lease and playback service for one file.</summary>
public sealed partial class VideoPlayerViewModel : ObservableObject, IDisposable
{
    private readonly IExternalMediaService _media;
    private readonly string _path;
    private readonly LibVlcPlaybackService _playback;
    private readonly VideoPlayerLocalizer _localizer;
    private PlaybackStatus _playbackStatus = PlaybackStatus.Preparing;
    private string? _statusDetail;

    public VideoPlayerViewModel(IExternalMediaService media, string path, LibVlcPlaybackService playback, VideoPlayerLocalizer localizer)
    {
        _media = media;
        _path = path;
        _playback = playback;
        _localizer = localizer;
        FileName = Path.GetFileName(path);
        _localizer.LanguageChanged += OnLanguageChanged;
        RefreshLocalizedText();
    }

    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _playPauseText = string.Empty;
    [ObservableProperty] private string _stopText = string.Empty;
    [ObservableProperty] private bool _canControlPlayback;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            SetStatus(PlaybackStatus.CreatingLease);
            var result = await _media.OpenPlaybackAsync(_path, cancellationToken);
            if (result.Status == AppCapabilityResult.PermissionDenied)
            {
                SetStatus(PlaybackStatus.PermissionDenied);
                return;
            }
            if (result.Status != AppCapabilityResult.Succeeded || result.Lease is null)
            {
                SetStatus(PlaybackStatus.Unavailable, result.Detail);
                return;
            }

            SetStatus(PlaybackStatus.Starting);
            _playback.Start(result.Lease.PlaybackUri, result.Lease);
            CanControlPlayback = true;
            SetStatus(PlaybackStatus.Playing);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SetStatus(PlaybackStatus.Failed, exception.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanControlPlayback))]
    private void TogglePlayPause()
    {
        _playback.TogglePlayPause();
        SetStatus(_playback.IsPlaying ? PlaybackStatus.Playing : PlaybackStatus.Paused);
    }

    [RelayCommand(CanExecute = nameof(CanControlPlayback))]
    private void Stop()
    {
        _playback.Stop();
        SetStatus(PlaybackStatus.Stopped);
    }

    public void Dispose() => _localizer.LanguageChanged -= OnLanguageChanged;

    partial void OnCanControlPlaybackChanged(bool value)
    {
        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs args) => RefreshLocalizedText();

    private void SetStatus(PlaybackStatus status, string? detail = null)
    {
        _playbackStatus = status;
        _statusDetail = detail;
        RefreshLocalizedText();
    }

    private void RefreshLocalizedText()
    {
        PlayPauseText = _localizer.Get("command.play_pause", "Play / Pause");
        StopText = _localizer.Get("command.stop", "Stop");
        StatusText = _playbackStatus switch
        {
            PlaybackStatus.Preparing => _localizer.Get("status.preparing", "Preparing video…"),
            PlaybackStatus.CreatingLease => _localizer.Get("status.creating_lease", "Creating a protected playback connection…"),
            PlaybackStatus.PermissionDenied => _localizer.Get("status.permission_denied", "Permission to read server files is not granted. Grant it in Settings > Applications > Video Player."),
            PlaybackStatus.Unavailable when string.IsNullOrWhiteSpace(_statusDetail) => _localizer.Get("status.unavailable", "The video is currently unavailable."),
            PlaybackStatus.Unavailable => _localizer.Format("status.unavailable_detail", "The video is currently unavailable: {0}", _statusDetail),
            PlaybackStatus.Starting => _localizer.Get("status.starting", "Starting VLC playback…"),
            PlaybackStatus.Playing => _localizer.Get("status.playing", "Playing"),
            PlaybackStatus.Paused => _localizer.Get("status.paused", "Paused"),
            PlaybackStatus.Stopped => _localizer.Get("status.stopped", "Stopped"),
            PlaybackStatus.Failed => _localizer.Format("status.failed", "Unable to play video: {0}", _statusDetail ?? string.Empty),
            _ => string.Empty,
        };
    }

    private enum PlaybackStatus
    {
        Preparing,
        CreatingLease,
        PermissionDenied,
        Unavailable,
        Starting,
        Playing,
        Paused,
        Stopped,
        Failed,
    }
}
