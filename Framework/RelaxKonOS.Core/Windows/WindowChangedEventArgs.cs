using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.Core.Windows;

public enum WindowChangeKind
{
    StateChanged,
    BoundsChanged,
    TitleChanged,
    FocusChanged
}

public sealed class WindowChangedEventArgs(WindowInfo window, WindowChangeKind kind) : EventArgs
{
    public WindowInfo Window { get; } = window;
    public WindowChangeKind Kind { get; } = kind;
    public WindowState State => Window.State;
    public Rect Bounds => Window.Bounds;
}
