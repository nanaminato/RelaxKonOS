using Avalonia;
using Avalonia.Media;

namespace RelaxKonOS.UI.Themes.SystemStyle;

/// <summary>
/// The four window-caption marks - minimize, maximize, restore and close - as geometry rather than
/// as characters, drawn on the grid the platforms themselves use.
/// </summary>
/// <remarks>
/// A self-drawn window cannot copy what Windows does here, because what Windows does is ship a font:
/// its caption marks are Segoe Fluent Icons (U+E921 / U+E922 / U+E923 / U+E8BB) drawn at 10px
/// <em>(segmdl2.ttf on Windows 10 carries the same four code points at the same metrics)</em>. That
/// font is Windows-only, cannot be redistributed, and its code points exist in no general-purpose
/// family - so a host that falls back to another font draws a different mark at a different weight,
/// or no mark at all. Standard Unicode characters are not a substitute either: a character carries
/// its designer's stroke weight, so a glyph that measures 2px thick can never be made to match a 1px
/// platform hairline, and its ink lands wherever the font's line box puts it rather than on the
/// button's centre line.
/// <para>
/// Drawing the marks instead makes them a property of the host, not of whichever font a host happens
/// to resolve: the shapes below are the platform's own, at the platform's own proportions, and every
/// coordinate is placed so that a 1px stroke covers whole pixels instead of smearing across two.
/// </para>
/// </remarks>
public static class WindowCaptionMarks
{
    /// <summary>
    /// Size of the box the platform's caption marks are drawn in - Segoe Fluent Icons at 10px. A
    /// mark box is what a profile varies (a 12px macOS traffic light carries a smaller mark than a
    /// 32px Windows caption); the shapes themselves do not change.
    /// </summary>
    public const double PlatformBox = 10;

    /// <summary>A 10-wide hairline. Windows centres it on the caption's middle row of pixels.</summary>
    public static Geometry Minimize(double box) =>
        Stroke((false, [new Point(0, Middle(box)), new Point(box, Middle(box))]));

    /// <summary>A hollow square: Windows' "maximize" is 10x10 with a 1px edge.</summary>
    public static Geometry Maximize(double box) =>
        Stroke((true, Rectangle(0, 0, box, box)));

    /// <summary>
    /// Windows' "restore": a front square with a back square showing behind its top-right corner.
    /// The back square loses the two edges the front one covers, which is why it is three points
    /// rather than a closed rectangle.
    /// </summary>
    public static Geometry Restore(double box)
    {
        // Overlap between the two squares. Two pixels on the platform's 10px box, and never less
        // than one: a smaller mark cannot show a step at all.
        var offset = Math.Max(1, Math.Round(box / 5));
        return Stroke(
            (false, [new Point(offset, 0.5), new Point(box - 0.5, 0.5), new Point(box - 0.5, box - offset - 0.5)]),
            (true, Rectangle(0, offset, box - offset, box)));
    }

    /// <summary>A cross spanning the whole box. Unlike the other three it is diagonal, so its ink is
    /// antialiased exactly as the platform's is.</summary>
    public static Geometry Close(double box)
    {
        // Corners included: a 1px-thick 45-degree band reaches half a pixel past its own centre line, so
        // the line starts and ends half a pixel in and the ink lands on the box exactly.
        const double half = 0.3535533905932738;
        var end = box - half;
        return Stroke(
            (false, [new Point(half, half), new Point(end, end)]),
            (false, [new Point(end, half), new Point(half, end)]));
    }

    /// <summary>
    /// The closed set of marks and the resource key each one is published under, so the host's
    /// resource builder and this file cannot drift apart.
    /// </summary>
    public static IReadOnlyList<(string Key, Func<double, Geometry> Build)> All { get; } =
    [
        (SystemStyleDerivedKeys.WindowCaptionMinimizeGlyph, Minimize),
        (SystemStyleDerivedKeys.WindowCaptionMaximizeGlyph, Maximize),
        (SystemStyleDerivedKeys.WindowCaptionRestoreGlyph, Restore),
        (SystemStyleDerivedKeys.WindowCaptionCloseGlyph, Close),
    ];

    /// <summary>Middle row of pixels of a <paramref name="box"/>-tall caption, as a stroke centre line.</summary>
    private static double Middle(double box) => Math.Floor(box / 2) + 0.5;

    /// <summary>Centre lines of a square whose 1px stroke covers cells <c>x..x+size-1</c> and <c>y..y+size-1</c>.</summary>
    private static Point[] Rectangle(double x, double y, double size, double bottom)
    {
        var right = x + size - 0.5;
        var lower = bottom - 0.5;
        return
        [
            new Point(x + 0.5, y + 0.5),
            new Point(right, y + 0.5),
            new Point(right, lower),
            new Point(x + 0.5, lower),
        ];
    }

    /// <summary>
    /// Builds stroked figures. Coordinates that fall on a half pixel put a 1px stroke inside one
    /// column or row of pixels, which is how these marks stay as crisp as the platform's; joins are
    /// mitred so a square outline keeps square corners.
    /// </summary>
    private static Geometry Stroke(params (bool Closed, Point[] Points)[] figures)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var (closed, points) in figures)
            {
                context.BeginFigure(points[0], isFilled: false);
                for (var index = 1; index < points.Length; index++)
                    context.LineTo(points[index]);
                context.EndFigure(closed);
            }
        }

        return geometry;
    }
}
