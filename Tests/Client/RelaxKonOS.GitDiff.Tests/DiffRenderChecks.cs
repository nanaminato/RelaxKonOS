using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using RelaxKonOS.Client.Controls.Diff;

namespace RelaxKonOS.GitDiff.Tests;

/// <summary>
/// Step 0 of the migration: can an AvaloniaEdit text view carry the colours, the CJK column
/// alignment and a ten-thousand-line patch with our own theme? Renders off-screen through
/// headless Skia and writes PNGs next to the executable.
/// </summary>
internal static class DiffRenderChecks
{
    private const string SamplePatch = """
diff --git a/src/map.ts b/src/map.ts
index 4bee98b..9a1c2f0 100644
--- a/src/map.ts
+++ b/src/map.ts
@@ -14,4 +14,11 @@ export async function highlightTheMatches(options) {
   if (s.visible) render(s, { color: s.color })
-  return hits
+  return hits.filter(Boolean)
   }
 }
+/** 绘制每颗卫星的轨道。 */
+export async function drawEachSatellite() {
+  const satellites = await api.position()
+  for (const s of satellites) {
+    if (s.visible) render(s, { color: s.color })
+  }
+}
""";

    public static void Run()
    {
        AppBuilder.Configure<DiffApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var output = Path.Combine(AppContext.BaseDirectory, "diff-qa");
        Directory.CreateDirectory(output);
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition)
                throw new InvalidOperationException($"Diff render check failed: {message}");
        }

        var document = DiffDocument.Parse(SamplePatch);
        Check(!document.IsEmpty && !document.IsIncomplete, "the sample patch parses completely");
        var sample = document.Files[0];
        var kinds = sample.Lines.Select(line => line.Kind).ToList();
        var text = string.Join('\n', sample.RawLines);

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var palette = DiffPalette.For(variant);
            var (window, editor) = CreateWindow(text, kinds, palette, variant, sample.DisplayPath, "+8 −1");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Check(editor.Document.LineCount == sample.Lines.Count, "every patch line is one document line");
            Check(editor.TextArea.TextView is { VisualLines.Count: > 0 }, "the view produced visual lines");
            using var bitmap = new RenderTargetBitmap(new PixelSize(980, 680));
            bitmap.Render(window);
            var path = Path.Combine(output, $"diff-{(variant == ThemeVariant.Dark ? "dark" : "light")}.png");
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            Check(new FileInfo(path).Length > 4000, "the bitmap holds rendered content");
            Check(CountTinted(bitmap, green: true) > 200, "added lines are drawn in a green tint");
            Check(CountTinted(bitmap, green: false) > 100, "removed lines are drawn in a red tint");
            window.Close();
        }

        // A patch the size of the server's 200 KB cap, to see whether the text view stays usable.
        var bigPatch = BuildBigPatch(400);
        var stopwatch = Stopwatch.StartNew();
        var bigDocument = DiffDocument.Parse(bigPatch);
        var parseMilliseconds = stopwatch.ElapsedMilliseconds;
        Check(!bigDocument.IsIncomplete, "the synthetic patch is complete");
        Check(bigDocument.Files[0].Hunks.Count == 400, "every hunk of the large patch is parsed");

        var bigKinds = bigDocument.Files[0].Lines.Select(line => line.Kind).ToList();
        stopwatch.Restart();
        var (bigWindow, bigEditor) = CreateWindow(
            string.Join('\n', bigDocument.Files[0].RawLines), bigKinds, DiffPalette.Light, ThemeVariant.Light, "big.cs", $"+{bigDocument.Additions} −{bigDocument.Deletions}");
        bigWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var loadMilliseconds = stopwatch.ElapsedMilliseconds;
        Check(bigEditor.Document.LineCount > 10_000, "the large patch becomes a ten-thousand-line document");
        Check(bigEditor.TextArea.TextView is { VisualLines.Count: > 0 }, "the large patch renders visible lines");
        Check(parseMilliseconds < 1500, $"parsing stays fast ({parseMilliseconds} ms)");
        Check(loadMilliseconds < 3000, $"loading stays fast ({loadMilliseconds} ms)");
        var bigPath = Path.Combine(output, "diff-large.png");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(980, 680)))
        {
            bitmap.Render(bigWindow);
            bitmap.Save(bigPath, PngBitmapEncoderOptions.Default);
        }

        bigWindow.Close();
        Console.WriteLine($"PASS: {checks} diff render checks. {bigEditor.Document.LineCount} lines parsed in {parseMilliseconds} ms, shown in {loadMilliseconds} ms. Screenshots: {output}");
    }

    private static (Window Window, TextEditor Editor) CreateWindow(
        string text,
        IReadOnlyList<DiffLineKind> kinds,
        DiffPalette palette,
        ThemeVariant variant,
        string path,
        string stats)
    {
        var editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            FontFamily = new FontFamily("Cascadia Code,Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"),
            FontSize = 12.5,
            WordWrap = false,
            Background = palette.Page,
            Foreground = palette.Text,
            Padding = new Thickness(0, 6, 0, 6),
        };
        editor.Text = text;
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(kinds, palette));
        editor.TextArea.TextView.LineTransformers.Add(new DiffLineColorizer(kinds, palette));

        var header = new Border
        {
            Background = palette.Page,
            BorderBrush = palette.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 8),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = path, FontSize = 13, Foreground = palette.Text, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = stats, FontSize = 12, Foreground = palette.Muted },
                },
            },
        };

        var host = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        host.Children.Add(header);
        host.Children.Add(editor);
        return (new Window
        {
            Width = 980,
            Height = 680,
            Content = host,
            RequestedThemeVariant = variant,
            Background = palette.Page,
        }, editor);
    }

    /// <summary>
    /// Counts pixels whose colour channel clearly dominates. Backgrounds are translucent enough to
    /// sit below the threshold, so a hit means the text itself was tinted, not just the row.
    /// </summary>
    private static int CountTinted(RenderTargetBitmap bitmap, bool green)
    {
        var area = new PixelRect(0, 0, 980, 680);
        var stride = area.Width * 4;
        var buffer = Marshal.AllocHGlobal(stride * area.Height);
        try
        {
            bitmap.CopyPixels(area, buffer, stride * area.Height, stride);
            var pixels = new byte[stride * area.Height];
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
            var count = 0;
            for (var index = 0; index + 3 < pixels.Length; index += 4)
            {
                int blue = pixels[index], channel = pixels[index + 1], red = pixels[index + 2];
                if (green ? channel > red + 24 && channel > blue + 24 : red > channel + 24 && red > blue + 24)
                    count++;
            }

            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string BuildBigPatch(int hunks)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("diff --git a/big.cs b/big.cs");
        text.AppendLine("--- a/big.cs");
        text.AppendLine("+++ b/big.cs");
        for (var hunk = 0; hunk < hunks; hunk++)
        {
            var start = hunk * 30 + 1;
            text.AppendLine($"@@ -{start},16 +{start},26 @@ public void Section{hunk}()");
            for (var i = 0; i < 10; i++)
                text.AppendLine($"  context {hunk}-{i} with some text to measure");
            for (var i = 0; i < 6; i++)
                text.AppendLine($"-  removed {hunk}-{i}");
            for (var i = 0; i < 16; i++)
                text.AppendLine($"+  added {hunk}-{i}");
        }

        return text.ToString();
    }
}

/// <summary>Backgrounds for diff lines, resolved per theme variant.</summary>
internal sealed record DiffPalette(
    IBrush Page,
    IBrush Text,
    IBrush Muted,
    IBrush Border,
    IBrush? Added,
    IBrush? Removed,
    IBrush? Hunk,
    IBrush? Metadata,
    IBrush? AddedInk,
    IBrush? RemovedInk,
    IBrush? MetaInk)
{
    public static DiffPalette Light { get; } = new(
        new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)),
        new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        new SolidColorBrush(Color.FromRgb(0xE2, 0xE6, 0xEA)),
        new SolidColorBrush(Color.FromArgb(0x30, 0x16, 0xA3, 0x4A)),
        new SolidColorBrush(Color.FromArgb(0x30, 0xDC, 0x26, 0x26)),
        new SolidColorBrush(Color.FromArgb(0x2E, 0x25, 0x63, 0xEB)),
        new SolidColorBrush(Color.FromArgb(0x1F, 0x6B, 0x72, 0x80)),
        new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)),
        new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)),
        new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));

    public static DiffPalette Dark { get; } = new(
        new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
        new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
        new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
        new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        new SolidColorBrush(Color.FromArgb(0x2A, 0x22, 0xC5, 0x5E)),
        new SolidColorBrush(Color.FromArgb(0x2E, 0xEF, 0x44, 0x44)),
        new SolidColorBrush(Color.FromArgb(0x33, 0x3B, 0x82, 0xF6)),
        new SolidColorBrush(Color.FromArgb(0x1F, 0x9C, 0xA3, 0xAF)),
        new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
        new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));

    public static DiffPalette For(ThemeVariant variant) => variant == ThemeVariant.Dark ? Dark : Light;

    public IBrush? BrushFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => Added,
        DiffLineKind.Removed => Removed,
        DiffLineKind.HunkHeader => Hunk,
        DiffLineKind.Metadata => Metadata,
        _ => null,
    };

    public IBrush? InkFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => AddedInk,
        DiffLineKind.Removed => RemovedInk,
        DiffLineKind.Metadata or DiffLineKind.HunkHeader or DiffLineKind.NoNewline => MetaInk,
        _ => null,
    };
}

/// <summary>
/// Colours a changed line's text. Step 2 replaces this with token colouring composed the same way:
/// a line transformer can apply syntax highlighting and then tint added or removed text.
/// </summary>
internal sealed class DiffLineColorizer(IReadOnlyList<DiffLineKind> kinds, DiffPalette palette) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var lineNumber = line.LineNumber;
        if (lineNumber < 1 || lineNumber > kinds.Count)
            return;
        if (palette.InkFor(kinds[lineNumber - 1]) is not { } brush)
            return;
        ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
    }
}

/// <summary>
/// Fills the full width of each changed line. Line kinds are indexed by document line number,
/// which is why the document must hold exactly one patch line per document line.
/// </summary>
internal sealed class DiffLineBackgroundRenderer(IReadOnlyList<DiffLineKind> kinds, DiffPalette palette) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.VisualLines.Count == 0)
            return;

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            if (lineNumber < 1 || lineNumber > kinds.Count)
                continue;
            if (palette.BrushFor(kinds[lineNumber - 1]) is not { } brush)
                continue;

            var segment = new TextSegment
            {
                StartOffset = visualLine.FirstDocumentLine.Offset,
                EndOffset = visualLine.LastDocumentLine.EndOffset,
            };
            // Backgrounds must cover the whole viewport, not just the text run, so an indented or
            // short line still reads as a band across the diff.
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                drawingContext.FillRectangle(brush, new Rect(0, rect.Y, textView.Bounds.Width, rect.Height));
        }
    }
}

public sealed class DiffApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
        Styles.Add(new StyleInclude(new Uri("avares://AvaloniaEdit/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });
        Styles.Add(new StyleInclude(new Uri("avares://RelaxKonOS.UI/"))
        {
            Source = new Uri("avares://RelaxKonOS.UI/Themes/RelaxKonOSTheme.axaml"),
        });
    }
}
