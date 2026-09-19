using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Controls;

/// <summary>A compact, theme-aware vector icon for Explorer entries and navigation nodes.</summary>
public sealed class ExplorerIcon : Control
{
    public static readonly StyledProperty<FileSystemEntryType?> EntryTypeProperty =
        AvaloniaProperty.Register<ExplorerIcon, FileSystemEntryType?>(nameof(EntryType));
    public static readonly StyledProperty<string?> FileNameProperty =
        AvaloniaProperty.Register<ExplorerIcon, string?>(nameof(FileName));
    public static readonly StyledProperty<TreeNodeIconKind?> TreeIconKindProperty =
        AvaloniaProperty.Register<ExplorerIcon, TreeNodeIconKind?>(nameof(TreeIconKind));
    public static readonly StyledProperty<IBrush?> FolderBrushProperty =
        AvaloniaProperty.Register<ExplorerIcon, IBrush?>(nameof(FolderBrush));
    public static readonly StyledProperty<IBrush?> DocumentBrushProperty =
        AvaloniaProperty.Register<ExplorerIcon, IBrush?>(nameof(DocumentBrush));
    public static readonly StyledProperty<IBrush?> MediaBrushProperty =
        AvaloniaProperty.Register<ExplorerIcon, IBrush?>(nameof(MediaBrush));
    public static readonly StyledProperty<IBrush?> CodeBrushProperty =
        AvaloniaProperty.Register<ExplorerIcon, IBrush?>(nameof(CodeBrush));
    public static readonly StyledProperty<IBrush?> ArchiveBrushProperty =
        AvaloniaProperty.Register<ExplorerIcon, IBrush?>(nameof(ArchiveBrush));
    public static readonly StyledProperty<IBrush?> ApplicationBrushProperty =
        AvaloniaProperty.Register<ExplorerIcon, IBrush?>(nameof(ApplicationBrush));
    public static readonly StyledProperty<IBrush?> NeutralBrushProperty =
        AvaloniaProperty.Register<ExplorerIcon, IBrush?>(nameof(NeutralBrush));

    static ExplorerIcon()
    {
        AffectsRender<ExplorerIcon>(EntryTypeProperty, FileNameProperty, TreeIconKindProperty, FolderBrushProperty,
            DocumentBrushProperty, MediaBrushProperty, CodeBrushProperty, ArchiveBrushProperty, ApplicationBrushProperty, NeutralBrushProperty);
    }

    public FileSystemEntryType? EntryType { get => GetValue(EntryTypeProperty); set => SetValue(EntryTypeProperty, value); }
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public TreeNodeIconKind? TreeIconKind { get => GetValue(TreeIconKindProperty); set => SetValue(TreeIconKindProperty, value); }
    public IBrush? FolderBrush { get => GetValue(FolderBrushProperty); set => SetValue(FolderBrushProperty, value); }
    public IBrush? DocumentBrush { get => GetValue(DocumentBrushProperty); set => SetValue(DocumentBrushProperty, value); }
    public IBrush? MediaBrush { get => GetValue(MediaBrushProperty); set => SetValue(MediaBrushProperty, value); }
    public IBrush? CodeBrush { get => GetValue(CodeBrushProperty); set => SetValue(CodeBrushProperty, value); }
    public IBrush? ArchiveBrush { get => GetValue(ArchiveBrushProperty); set => SetValue(ArchiveBrushProperty, value); }
    public IBrush? ApplicationBrush { get => GetValue(ApplicationBrushProperty); set => SetValue(ApplicationBrushProperty, value); }
    public IBrush? NeutralBrush { get => GetValue(NeutralBrushProperty); set => SetValue(NeutralBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var kind = TreeIconKind is { } treeKind
            ? ExplorerFileIconKindResolver.ForTreeNode(treeKind)
            : ExplorerFileIconKindResolver.ForEntry(EntryType ?? FileSystemEntryType.File, FileName);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 20d;
        if (scale <= 0) return;

        var x = (Bounds.Width - 20 * scale) / 2;
        var y = (Bounds.Height - 20 * scale) / 2;
        Rect R(double left, double top, double width, double height) => new(x + left * scale, y + top * scale, width * scale, height * scale);
        Point P(double px, double py) => new(x + px * scale, y + py * scale);
        var primary = BrushFor(kind) ?? NeutralBrush;
        if (primary is null) return;
        var detail = NeutralBrush ?? primary;
        var thin = new Pen(detail, Math.Max(0.9, scale));

        switch (kind)
        {
            case ExplorerFileIconKind.Folder:
            case ExplorerFileIconKind.Downloads:
                context.DrawRectangle(primary, null, R(2, 6, 16, 11), 2 * scale, 2 * scale);
                context.DrawRectangle(primary, null, R(3, 3.5, 7.5, 4), 1.5 * scale, 1.5 * scale);
                if (kind == ExplorerFileIconKind.Downloads)
                {
                    context.DrawLine(thin, P(10, 7.5), P(10, 13));
                    context.DrawLine(thin, P(7.8, 10.8), P(10, 13));
                    context.DrawLine(thin, P(12.2, 10.8), P(10, 13));
                }
                break;
            case ExplorerFileIconKind.Drive:
                context.DrawRectangle(primary, null, R(2, 5, 16, 10), 2 * scale, 2 * scale);
                context.DrawRectangle(null, thin, R(3.2, 7, 13.6, 5));
                context.DrawEllipse(detail, null, P(14, 11.2), 1.1 * scale, 1.1 * scale);
                break;
            case ExplorerFileIconKind.Home:
                DrawHouse(context, primary, detail, P, scale);
                break;
            case ExplorerFileIconKind.Desktop:
                context.DrawRectangle(primary, null, R(2.5, 3.5, 15, 11), 1.5 * scale, 1.5 * scale);
                context.DrawLine(thin, P(7, 17), P(13, 17));
                context.DrawLine(thin, P(10, 14.5), P(10, 17));
                break;
            case ExplorerFileIconKind.Network:
                context.DrawEllipse(null, thin, P(10, 10), 7 * scale, 7 * scale);
                context.DrawLine(thin, P(3.2, 10), P(16.8, 10));
                context.DrawLine(thin, P(10, 3.2), P(10, 16.8));
                break;
            case ExplorerFileIconKind.DiskImage:
                context.DrawEllipse(primary, null, P(10, 10), 7.3 * scale, 7.3 * scale);
                context.DrawEllipse(null, thin, P(10, 10), 2.1 * scale, 2.1 * scale);
                break;
            case ExplorerFileIconKind.Archive:
                context.DrawRectangle(primary, null, R(3, 3, 14, 14), 2 * scale, 2 * scale);
                for (var i = 0; i < 4; i++) context.DrawRectangle(detail, null, R(9, 5 + i * 3, 2, 1.6));
                break;
            case ExplorerFileIconKind.Application:
                context.DrawRectangle(primary, null, R(2.5, 3, 15, 14), 2 * scale, 2 * scale);
                context.DrawLine(thin, P(2.8, 6.5), P(17.2, 6.5));
                context.DrawEllipse(detail, null, P(5, 4.8), .75 * scale, .75 * scale);
                context.DrawLine(thin, P(7.2, 10), P(9.2, 12));
                context.DrawLine(thin, P(9.2, 12), P(7.2, 14));
                context.DrawLine(thin, P(12.8, 10), P(10.8, 12));
                context.DrawLine(thin, P(10.8, 12), P(12.8, 14));
                break;
            case ExplorerFileIconKind.Database:
                context.DrawEllipse(primary, null, P(10, 5), 6.5 * scale, 2.7 * scale);
                context.DrawRectangle(primary, null, R(3.5, 5, 13, 9));
                context.DrawEllipse(primary, null, P(10, 14), 6.5 * scale, 2.7 * scale);
                context.DrawEllipse(null, thin, P(10, 5), 6.5 * scale, 2.7 * scale);
                break;
            case ExplorerFileIconKind.Font:
                DrawDocument(context, primary, detail, R, P, thin, "A", scale);
                break;
            case ExplorerFileIconKind.Image:
                DrawDocument(context, primary, detail, R, P, thin, "I", scale);
                break;
            case ExplorerFileIconKind.Audio:
                DrawDocument(context, primary, detail, R, P, thin, "♪", scale);
                break;
            case ExplorerFileIconKind.Video:
                DrawDocument(context, primary, detail, R, P, thin, "▶", scale);
                break;
            case ExplorerFileIconKind.Spreadsheet:
                DrawDocument(context, primary, detail, R, P, thin, "▦", scale);
                break;
            case ExplorerFileIconKind.Presentation:
                DrawDocument(context, primary, detail, R, P, thin, "▤", scale);
                break;
            case ExplorerFileIconKind.Pdf:
                DrawDocument(context, primary, detail, R, P, thin, "PDF", scale);
                break;
            case ExplorerFileIconKind.Code:
                DrawDocument(context, primary, detail, R, P, thin, "</>", scale);
                break;
            case ExplorerFileIconKind.Data:
                DrawDocument(context, primary, detail, R, P, thin, "{}", scale);
                break;
            default:
                DrawDocument(context, primary, detail, R, P, thin, kind == ExplorerFileIconKind.Document ? "" : "·", scale);
                break;
        }
    }

    private IBrush? BrushFor(ExplorerFileIconKind kind) => kind switch
    {
        ExplorerFileIconKind.Folder or ExplorerFileIconKind.Downloads => FolderBrush,
        ExplorerFileIconKind.Image or ExplorerFileIconKind.Audio or ExplorerFileIconKind.Video => MediaBrush,
        ExplorerFileIconKind.Archive => ArchiveBrush,
        ExplorerFileIconKind.Application or ExplorerFileIconKind.DiskImage => ApplicationBrush,
        ExplorerFileIconKind.Code or ExplorerFileIconKind.Data or ExplorerFileIconKind.Database => CodeBrush,
        ExplorerFileIconKind.Drive or ExplorerFileIconKind.Network => NeutralBrush,
        _ => DocumentBrush
    };

    private static void DrawDocument(DrawingContext context, IBrush primary, IBrush detail, Func<double, double, double, double, Rect> r, Func<double, double, Point> p, Pen thin, string mark, double scale)
    {
        context.DrawRectangle(primary, null, r(4, 2, 12, 16), 1.5 * scale, 1.5 * scale);
        context.DrawLine(thin, p(12, 2.3), p(12, 6));
        context.DrawLine(thin, p(12, 6), p(15.5, 6));
        if (!string.IsNullOrWhiteSpace(mark))
        {
            var glyph = new FormattedText(mark, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI, Inter, Noto Sans", FontStyle.Normal, FontWeight.Bold), mark.Length > 2 ? 4.3 : 7, detail);
            context.DrawText(glyph, p(mark.Length > 2 ? 5.1 : 7.4, 6.5));
        }
    }

    private static void DrawHouse(DrawingContext context, IBrush primary, IBrush detail, Func<double, double, Point> p, double scale)
    {
        var geometry = new StreamGeometry();
        using (var shape = geometry.Open())
        {
            shape.BeginFigure(p(3, 9), true);
            shape.LineTo(p(10, 3), true);
            shape.LineTo(p(17, 9), true);
            shape.LineTo(p(15.5, 9), true);
            shape.LineTo(p(15.5, 17), true);
            shape.LineTo(p(4.5, 17), true);
            shape.LineTo(p(4.5, 9), true);
            shape.EndFigure(true);
        }
        context.DrawGeometry(primary, null, geometry);
        context.DrawRectangle(detail, null, new Rect(p(8, 12), new Size(4 * scale, 5 * scale)), scale, scale);
    }
}
