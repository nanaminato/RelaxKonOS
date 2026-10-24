using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RelaxKonOS.Client.Controls.Diff;

/// <summary>
/// Fills the full width of a changed row, gutter band included when the gutter draws its own.
/// </summary>
/// <remarks>
/// The band is painted from the text view's left edge to its right edge rather than from the text
/// run, because an indented or short line has to read as one continuous stripe — and because the
/// add/remove signal must not disappear when a line is narrower than the viewport.
/// </remarks>
internal sealed class DiffLineBackgroundRenderer(Func<DiffRowMap> rows, DiffStyle style) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.VisualLines.Count == 0)
            return;

        var map = rows();
        if (map.Rows.Count == 0)
            return;

        var width = textView.Bounds.Width;
        foreach (var visualLine in textView.VisualLines)
        {
            var row = map.At(visualLine.FirstDocumentLine.LineNumber);
            if (style.BandFor(row.Kind) is not { } brush)
                continue;

            var top = visualLine.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(brush, new Rect(0, top, width, visualLine.Height));
        }
    }
}

/// <summary>Colours the text of a row: red and green for changes, muted for structure.</summary>
internal sealed class DiffLineInkTransformer(Func<DiffRowMap> rows, DiffStyle style) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var map = rows();
        if (map.Rows.Count == 0)
            return;

        if (style.InkFor(map.At(line.LineNumber).Kind) is not { } brush)
            return;

        ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
    }
}
