using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RelaxKonOS.Client.Apps.TaskManager.Controls;

/// <summary>Small, dependency-free performance graph used by the task manager.</summary>
public sealed class PerformanceLineChart : Control
{
    public static readonly StyledProperty<IEnumerable<double>?> ValuesProperty =
        AvaloniaProperty.Register<PerformanceLineChart, IEnumerable<double>?>(nameof(Values));

    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<PerformanceLineChart, Color>(nameof(LineColor), Color.Parse("#0078D4"));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<PerformanceLineChart, double>(nameof(Maximum), 100);

    private INotifyCollectionChanged? _observableValues;

    static PerformanceLineChart()
    {
        ValuesProperty.Changed.AddClassHandler<PerformanceLineChart>((chart, change) =>
            chart.OnValuesChanged(change.OldValue as IEnumerable<double>, change.NewValue as IEnumerable<double>));
    }

    public IEnumerable<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Color LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = Bounds.Deflate(0.5);
        if (rect.Width <= 1 || rect.Height <= 1) return;

        var lineBrush = new SolidColorBrush(LineColor);
        var gridBrush = new SolidColorBrush(Color.FromArgb(110, LineColor.R, LineColor.G, LineColor.B));
        var border = new Pen(lineBrush, 1);
        var grid = new Pen(gridBrush, 1);
        context.DrawRectangle(null, border, rect);

        for (var column = 1; column < 12; column++)
        {
            var x = rect.Left + rect.Width * column / 12;
            context.DrawLine(grid, new Point(x, rect.Top), new Point(x, rect.Bottom));
        }
        for (var row = 1; row < 6; row++)
        {
            var y = rect.Top + rect.Height * row / 6;
            context.DrawLine(grid, new Point(rect.Left, y), new Point(rect.Right, y));
        }

        var values = Values?.ToArray() ?? [];
        if (values.Length < 2) return;

        var maximum = Math.Max(Maximum, 1);
        var fill = new SolidColorBrush(Color.FromArgb(28, LineColor.R, LineColor.G, LineColor.B));
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(rect.Left, rect.Bottom), true);
            for (var index = 0; index < values.Length; index++)
                figure.LineTo(ToPoint(index, values, rect, maximum));
            figure.LineTo(new Point(rect.Right, rect.Bottom));
            figure.EndFigure(true);
        }
        context.DrawGeometry(fill, null, geometry);

        var line = new Pen(lineBrush, 1.25);
        for (var index = 1; index < values.Length; index++)
            context.DrawLine(line, ToPoint(index - 1, values, rect, maximum), ToPoint(index, values, rect, maximum));
    }

    private void OnValuesChanged(IEnumerable<double>? oldValue, IEnumerable<double>? newValue)
    {
        if (_observableValues is not null) _observableValues.CollectionChanged -= OnCollectionChanged;
        _observableValues = newValue as INotifyCollectionChanged;
        if (_observableValues is not null) _observableValues.CollectionChanged += OnCollectionChanged;
        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private static Point ToPoint(int index, IReadOnlyList<double> values, Rect rect, double maximum)
    {
        var x = rect.Left + rect.Width * index / Math.Max(values.Count - 1, 1);
        var ratio = Math.Clamp(values[index] / maximum, 0, 1);
        return new Point(x, rect.Bottom - rect.Height * ratio);
    }
}
