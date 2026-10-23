using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RelaxKonOS.Client.Controls.Diff;

/// <summary>
/// Two-column gutter: the old line number on the left, the new one on the right.
/// </summary>
/// <remarks>
/// A unified diff has two numbering spaces, so the stock <see cref="LineNumberMargin"/> cannot be
/// reused — it prints one number per document line, and the document line number is the position
/// inside the patch, not either side of the change. Deriving from <see cref="AbstractMargin"/>
/// keeps the scrolling, horizontal clamping and font inheritance the editor already provides.
/// </remarks>
internal sealed class DiffLineNumberMargin : AbstractMargin
{
    private const double LeftPadding = 6;
    private const double RightPadding = 10;
    private const double ColumnGap = 8;

    private readonly Func<DiffRowMap> _rows;
    private readonly DiffStyle _style;
    private Typeface _typeface = new(FontFamily.Default);
    private double _emSize = 12;
    private double _digitsWidth;
    private int _oldDigits = 1;
    private int _newDigits = 1;

    public DiffLineNumberMargin(Func<DiffRowMap> rows, DiffStyle style)
    {
        _rows = rows;
        _style = style;
        // The gutter is a chrome band of its own, not a transparent window: without a background
        // the text view's own surface shows through and the row bands stop at the gutter edge.
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The font is inherited from the text area, so the gutter always matches the code beside
        // it — including when the editor's font size is bound, as the code editor's is.
        _typeface = new Typeface(
            GetValue(TextBlock.FontFamilyProperty),
            GetValue(TextBlock.FontStyleProperty),
            GetValue(TextBlock.FontWeightProperty));
        _emSize = GetValue(TextBlock.FontSizeProperty);

        var map = _rows();
        _oldDigits = map.OldDigits;
        _newDigits = map.NewDigits;
        _digitsWidth = Math.Max(MeasureDigits(_oldDigits), MeasureDigits(_newDigits));

        var width = LeftPadding + (_digitsWidth * 2) + ColumnGap + RightPadding;
        return new Size(width, 0);
    }

    public override void Render(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView is not { VisualLinesValid: true })
            return;

        var map = _rows();
        if (map.Rows.Count == 0)
            return;

        var width = Bounds.Width;
        foreach (var visualLine in textView.VisualLines)
        {
            var row = map.At(visualLine.FirstDocumentLine.LineNumber);
            var top = visualLine.VisualTop - textView.VerticalOffset;

            if (_style.BandFor(row.Kind) is { } band)
                drawingContext.FillRectangle(band, new Rect(0, top, width, visualLine.Height));

            // Text is positioned against the first text line, so a wrapped or tall line keeps both
            // numbers on the same baseline as the text they belong to.
            var textTop = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
            if (row.OldNumber is { } old)
                DrawNumber(drawingContext, old, 0, _style.NumberInk(row.Kind, oldSide: true), textTop);
            if (row.NewNumber is { } current)
                DrawNumber(drawingContext, current, 1, _style.NumberInk(row.Kind, oldSide: false), textTop);
        }

        drawingContext.DrawLine(
            new Pen(_style.Separator ?? Brushes.Transparent, 1),
            new Point(width - 0.5, 0),
            new Point(width - 0.5, Bounds.Height));
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.ScrollOffsetChanged -= OnTextViewScrolled;
            oldTextView.VisualLinesChanged -= OnTextViewScrolled;
        }

        base.OnTextViewChanged(oldTextView, newTextView);

        if (newTextView is not null)
        {
            newTextView.ScrollOffsetChanged += OnTextViewScrolled;
            newTextView.VisualLinesChanged += OnTextViewScrolled;
        }

        InvalidateMeasure();
    }

    private void OnTextViewScrolled(object? sender, EventArgs args) => InvalidateVisual();

    private void DrawNumber(DrawingContext drawingContext, int number, int column, IBrush? brush, double top)
    {
        if (brush is null)
            return;

        var text = CreateText(number.ToString(CultureInfo.CurrentCulture), brush);
        var columnRight = LeftPadding + _digitsWidth + (column * (ColumnGap + _digitsWidth));
        drawingContext.DrawText(text, new Point(columnRight - text.Width, top));
    }

    private double MeasureDigits(int digits) => CreateText(new string('9', Math.Max(1, digits)), null).Width;

    private FormattedText CreateText(string text, IBrush? brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _emSize, brush);
}
