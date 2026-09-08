namespace RelaxKonOS.Core.Primitives;

/// <summary>Immutable size in density-independent pixels.</summary>
public readonly record struct Size(double Width, double Height)
{
    public static readonly Size Zero = new(0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public Size Clamp(Size min, Size max) => new(
        Math.Clamp(Width, min.Width, max.Width),
        Math.Clamp(Height, min.Height, max.Height));

    public override string ToString() => $"{Width:0.#}x{Height:0.#}";
}
