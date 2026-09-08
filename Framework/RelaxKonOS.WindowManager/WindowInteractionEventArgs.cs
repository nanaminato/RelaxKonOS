using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.WindowManager;

/// <summary>Carries the user's drag delta (desktop-space) relative to the press start.</summary>
public sealed class DragBoundsEventArgs(Rect startBounds, Point delta) : EventArgs
{
    public Rect StartBounds { get; } = startBounds;
    public Point Delta { get; } = delta;
}

/// <summary>Carries the user's resize delta (desktop-space) relative to the press start.</summary>
public sealed class ResizeBoundsEventArgs(ResizeEdge edge, Rect startBounds, Point delta) : EventArgs
{
    public ResizeEdge Edge { get; } = edge;
    public Rect StartBounds { get; } = startBounds;
    public Point Delta { get; } = delta;
}
