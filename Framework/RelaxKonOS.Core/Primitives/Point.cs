namespace RelaxKonOS.Core.Primitives;

/// <summary>Immutable 2D point in desktop coordinates (density-independent pixels).</summary>
public readonly record struct Point(double X, double Y)
{
    public static readonly Point Zero = new(0, 0);

    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    public override string ToString() => $"({X:0.#}, {Y:0.#})";
}
