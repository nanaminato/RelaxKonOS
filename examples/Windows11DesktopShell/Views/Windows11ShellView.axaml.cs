using Avalonia.Controls;

namespace Example.Windows11DesktopShell.Views;

public partial class Windows11ShellView : UserControl
{
    public Windows11ShellView() => InitializeComponent();

    public Canvas WindowHostSurface => WindowHost;
    public Canvas FullScreenHostSurface => FullScreenHost;
    public Panel ShellOverlaySurface => ShellOverlayHost;
    public Control InputBackdropSurface => InputBackdrop;
}
