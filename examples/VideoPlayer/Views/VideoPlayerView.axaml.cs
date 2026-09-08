using Avalonia.Controls;
using LibVLCSharp.Avalonia;

namespace RelaxKonOS.Examples.VideoPlayer.Views;

public partial class VideoPlayerView : UserControl
{
    public VideoPlayerView() => InitializeComponent();

    public VideoView VideoSurface => PlayerSurface;
}
