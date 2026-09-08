namespace RelaxKonOS.WindowManager;

/// <summary>Edge(s) of a window being resized by the user.</summary>
[Flags]
public enum ResizeEdge
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    North = Top,
    South = Bottom,
    East = Right,
    West = Left,
    NorthWest = Top | Left,
    NorthEast = Top | Right,
    SouthWest = Bottom | Left,
    SouthEast = Bottom | Right,
}
