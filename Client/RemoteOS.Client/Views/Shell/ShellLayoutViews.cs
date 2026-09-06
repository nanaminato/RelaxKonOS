using Avalonia.Controls;

namespace Client.Views.Shell;

/// <summary>
/// AXAML-backed layout hosts for the built-in shells.  The shell runtime injects the desktop
/// surface and chrome controls, while geometry and z-order remain editable in the matching AXAML.
/// </summary>
public partial class WindowsShellLayoutView : UserControl
{
    public WindowsShellLayoutView() => InitializeComponent();

    public void Compose(Control desktop, Control taskbar, Control launcher)
    {
        DesktopHost.Content = desktop;
        TaskbarHost.Content = taskbar;
        LauncherHost.Content = launcher;
    }
}

public partial class MacosShellLayoutView : UserControl
{
    public MacosShellLayoutView() => InitializeComponent();

    public void Compose(Control menuBar, Control desktop, Control dock, Control launcher)
    {
        MenuBarHost.Content = menuBar;
        DesktopHost.Content = desktop;
        DockHost.Content = dock;
        LauncherHost.Content = launcher;
    }
}

public partial class UbuntuShellLayoutView : UserControl
{
    public UbuntuShellLayoutView() => InitializeComponent();

    public void Compose(Control topBar, Control dock, Control desktop, Control launcher)
    {
        TopBarHost.Content = topBar;
        DockHost.Content = dock;
        DesktopHost.Content = desktop;
        LauncherHost.Content = launcher;
    }
}
