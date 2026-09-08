namespace RelaxKonOS.Core.Windows;

/// <summary>Visual state of a managed window, mirroring classic OS window states.</summary>
public enum WindowState
{
    Normal,
    Minimized,
    Maximized,
    /// <summary>Fills the entire desktop, including shell chrome such as the taskbar.</summary>
    FullScreen
}
