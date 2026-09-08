using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Examples.VideoPlayer.Services;
using RelaxKonOS.Examples.VideoPlayer.ViewModels;
using RelaxKonOS.Examples.VideoPlayer.Views;
using RemoteRect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Examples.VideoPlayer;

/// <summary>Composition root for the Video Player development package.</summary>
public sealed class VideoPlayerApp : IExternalRemoteApplication, IExternalFileOpenApplication
{
    public ApplicationManifest Manifest { get; } = new(
        new AppId("com.remoteos.example.video-player"),
        "Video Player",
        "0.2.1-dev",
        "🎞️",
        "VLC-based remote video player for Windows",
        [AppPermissions.ServerFilesRead],
        LocalizedMetadata: new Dictionary<string, ApplicationLocalizedMetadata>
        {
            ["en-US"] = new("Video Player", "VLC-based remote video player for Windows"),
            ["zh-CN"] = new("视频播放器", "基于 VLC 的 Windows 远程视频播放器"),
            ["ja-JP"] = new("ビデオ プレーヤー", "Windows 向け VLC ベースのリモート ビデオ プレーヤー"),
        });

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        var localizer = new VideoPlayerLocalizer(context.SystemLanguage);
        var viewModel = new VideoPlayerHomeViewModel(localizer);
        var view = new VideoPlayerHomeView { DataContext = viewModel };
        var window = context.Windows.ShowWindow(localizer.Get("app.name", "Video Player"), view,
            new RemoteRect(190, 130, 540, 250), Manifest.IconGlyph);
        BindWindowTitle(window, localizer);
        window.Closed.Register(viewModel.Dispose);
        return Task.CompletedTask;
    }

    public Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default)
    {
        var localizer = new VideoPlayerLocalizer(context.SystemLanguage);
        var playback = new LibVlcPlaybackService();
        var viewModel = new VideoPlayerViewModel(context.Media, path, playback, localizer);
        var view = new VideoPlayerView { DataContext = viewModel };
        playback.Attach(view.VideoSurface);
        var window = context.Windows.ShowWindow(localizer.Get("app.name", "Video Player"), view,
            new RemoteRect(130, 80, 960, 640), Manifest.IconGlyph);
        BindWindowTitle(window, localizer);
        window.Closed.Register(viewModel.Dispose);
        _ = new VideoPlayerWindowLifetime(window, playback);
        _ = viewModel.OpenAsync(window.Closed);
        return Task.CompletedTask;
    }

    private static void BindWindowTitle(IExternalAppWindowHandle window, VideoPlayerLocalizer localizer)
    {
        EventHandler refreshTitle = (_, _) => window.Window.Title = localizer.Get("app.name", "Video Player");
        localizer.LanguageChanged += refreshTitle;
        window.Closed.Register(() =>
        {
            localizer.LanguageChanged -= refreshTitle;
            localizer.Dispose();
        });
    }
}
