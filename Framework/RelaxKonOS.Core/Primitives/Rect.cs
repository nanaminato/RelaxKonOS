namespace RelaxKonOS.Core.Primitives;

/// <summary>Immutable rectangle in desktop coordinates.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public Point Position => new(X, Y);
    public Size Size => new(Width, Height);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(Point p) =>
        p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public Rect WithPosition(Point p) => new(p.X, p.Y, Width, Height);
    public Rect WithSize(Size s) => new(X, Y, s.Width, s.Height);

    public Rect Clamp(Rect bounds) => new(
        Math.Clamp(X, bounds.X, Math.Max(bounds.X, bounds.Right - Width)),
        Math.Clamp(Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - Height)),
        Width, Height);

    public override string ToString() => $"({X:0.#},{Y:0.#} {Width:0.#}x{Height:0.#})";
}
