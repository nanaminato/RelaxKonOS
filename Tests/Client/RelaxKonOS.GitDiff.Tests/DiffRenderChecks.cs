using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.Git;
using RelaxKonOS.Client.Apps.Git.Views;
using RelaxKonOS.Client.Controls.Diff;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;

namespace RelaxKonOS.GitDiff.Tests;

/// <summary>
/// Renders the real <see cref="DiffView"/> off-screen and inspects the bitmap: the row band has to
/// reach both the gutter and the right edge, the gutter has to carry two number columns, and the
/// text has to be tinted. None of that is visible to an assertion that only looks at properties.
/// </summary>
internal static class DiffRenderChecks
{
    private const int ViewWidth = 980;
    private const int ViewHeight = 680;

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

        using var localization = BuildLocalization();

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            ApplyPalette(variant);
            var accountsVm = new GitClientViewModel(null!);
            accountsVm.CredentialAccounts.Add(new("first", "github.com", "alice", true));
            accountsVm.CredentialAccounts.Add(new("second", "gitlab.example.com:8443", "work-account", true));
            accountsVm.SelectedCredentialAccount = accountsVm.CredentialAccounts[1];
            var accountsView = new GitCredentialAccountsView { DataContext = accountsVm };
            var accountsWindow = new Window { Width = 760, Height = 600, Content = accountsView };
            accountsWindow.Show(); Dispatcher.UIThread.RunJobs();
            Check(accountsView.GetVisualDescendants().OfType<ListBox>().Single().ItemCount == 2, "account manager displays saved metadata");
            var accountScroll = accountsView.GetVisualDescendants().OfType<ScrollViewer>().First();
            Check(accountScroll.Extent.Width <= accountScroll.Viewport.Width + 1, "account manager wraps content without horizontal clipping");
            Check(accountsView.GetVisualDescendants().OfType<TextBox>().Any(x => x.PasswordChar == '•'), "account token input is masked");
            using var bitmap = new RenderTargetBitmap(new PixelSize(760, 600));
            bitmap.Render(accountsWindow);
            bitmap.Save(Path.Combine(output, "git-accounts-" + variant + ".png"), PngBitmapEncoderOptions.Default);
            accountsWindow.Close();
        }

        var document = DiffDocument.Parse(SamplePatch);
        Check(!document.IsEmpty && !document.IsIncomplete, "the sample patch parses completely");
        var rows = document.Files[0].Lines;
        var addedRow = RowOf(rows, DiffLineKind.Added);
        var removedRow = RowOf(rows, DiffLineKind.Removed);
        var contextRow = RowOf(rows, DiffLineKind.Context);

        var surfaces = new Dictionary<string, Color>();
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var colors = ApplyPalette(variant);
            var surface = Color.Parse(colors["SurfaceSunken"]);
            surfaces[variant.ToString()] = surface;

            var view = new DiffView { Document = document };
            var (window, editor) = CreateWindow(view, surface);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var textView = editor.TextArea.TextView;
            var gutter = editor.TextArea.LeftMargins.OfType<DiffLineNumberMargin>().Single();
            Check(editor.Document.LineCount == rows.Count, "every patch line is one document line");
            Check(gutter.Bounds.Width > 24, $"the gutter reserved room for two columns ({gutter.Bounds.Width:F0}px)");
            Check(textView is { VisualLines.Count: > 0 }, "the view produced visual lines");

            using var bitmap = new RenderTargetBitmap(new PixelSize(ViewWidth, ViewHeight));
            bitmap.Render(window);
            var name = variant == ThemeVariant.Dark ? "dark" : "light";
            var path = Path.Combine(output, $"diff-{name}.png");
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            Check(new FileInfo(path).Length > 4000, "the bitmap holds rendered content");

            var snapshot = new Snapshot(bitmap, ViewWidth, ViewHeight);
            var lineHeight = textView.DefaultLineHeight;
            var gutterWidth = (int)gutter.Bounds.Width;

            // The band is what makes a change scannable, so it must span the whole row: gutter
            // included, right edge included. A band that stops at the text run is the defect this
            // check exists for.
            var addedRowBand = RowBand(snapshot, textView, addedRow, lineHeight);
            Check(addedRowBand.Fraction(c => IsGreenish(c)) > 0.5, "the added row is green across its full width");

            var removedRowBand = RowBand(snapshot, textView, removedRow, lineHeight);
            Check(removedRowBand.Fraction(c => IsReddish(c)) > 0.5, "the removed row is red across its full width");

            var contextBand = RowBand(snapshot, textView, contextRow, lineHeight);
            Check(contextBand.Fraction(c => !IsGreenish(c) && !IsReddish(c)) > 0.9, "an unchanged row keeps the surface colour");

            // Two numbering spaces mean two columns; an empty half would mean the old or the new
            // side silently stopped being rendered.
            var gutterBand = RowBand(snapshot, textView, contextRow, lineHeight);
            var leftHalf = gutterBand.Sub(0, gutterWidth / 2);
            var rightHalf = gutterBand.Sub(gutterWidth / 2, gutterWidth / 2);
            Check(leftHalf.Count(c => Distance(c, surface) > 45) > 20, "the old-side column shows numbers");
            Check(rightHalf.Count(c => Distance(c, surface) > 45) > 20, "the new-side column shows numbers");

            Check(snapshot.Count(c => IsGreenish(c) && Luminance(c) < 150) > 40, "added text is drawn in a green tint");
            Check(snapshot.Count(c => IsReddish(c) && Luminance(c) < 150) > 20, "removed text is drawn in a red tint");

            // A palette swap replaces the colour dictionary as a whole, so an already-open diff has
            // to repaint from the new tokens. Baking brushes at construction would leave it light.
            if (variant == ThemeVariant.Light)
            {
                ApplyPalette(ThemeVariant.Dark);
                Dispatcher.UIThread.RunJobs();
                using var switchedBitmap = new RenderTargetBitmap(new PixelSize(ViewWidth, ViewHeight));
                switchedBitmap.Render(window);
                switchedBitmap.Save(Path.Combine(output, "diff-theme-switch.png"), PngBitmapEncoderOptions.Default);
                var switchedSnapshot = new Snapshot(switchedBitmap, ViewWidth, ViewHeight);
                var switchedBand = RowBand(switchedSnapshot, textView, addedRow, lineHeight);
                Check(
                    switchedBand.Fraction(c => IsGreenish(c) && Luminance(c) < 140) > 0.5,
                    "an open diff repaints its row bands when the palette switches");
                ApplyPalette(ThemeVariant.Light);
                Dispatcher.UIThread.RunJobs();
            }

            window.Close();
        }

        Check(!surfaces.Values.First().Equals(surfaces.Values.Last()), "the two renders use different palettes");

        // An empty patch must not throw and must not reserve a gutter for numbers that cannot exist.
        var emptyWindow = CreateWindow(new DiffView { Document = DiffDocument.Parse(null) }, surfaces.Values.First());
        emptyWindow.Window.Show();
        Dispatcher.UIThread.RunJobs();
        Check(emptyWindow.Editor.Document.LineCount == 1 && emptyWindow.Editor.Text.Length == 0, "an empty patch renders an empty document");
        emptyWindow.Window.Close();

        // The pane is shared by the modal dialog and the log page's inline column, so its bindings
        // (path, both counts, the close mark the host opts into) are worth a render rather than a
        // reading — a renamed property would otherwise only show up as a blank header at runtime.
        // BuildLocalization() has to run first: the presentation resolves its captions the same way
        // every other view model does, through the application service provider.
        var presentation = new GitDiffPresentation(new GitDiffDto("src/map.ts", "v1", null, SamplePatch, 8, 1));
        var pane = new GitDiffPane { DataContext = presentation, CanClose = true };
        var paneWindow = new Window { Width = ViewWidth, Height = ViewHeight, Content = pane };
        paneWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var paneTexts = pane.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Check(paneTexts.Contains("src/map.ts"), "the pane header shows the file path");
        Check(paneTexts.Contains("+8") && paneTexts.Contains("−1"), "the pane header shows both change counts");
        Check(
            pane.GetVisualDescendants().OfType<DiffView>().Single().Document?.Files[0].Lines.Count == rows.Count,
            "the pane hands the parsed patch to the diff view");
        var closeMark = pane.GetVisualDescendants().OfType<Button>().Single();
        Check(closeMark.IsVisible, "a pane that may close shows its close mark");
        using (var paneBitmap = new RenderTargetBitmap(new PixelSize(ViewWidth, ViewHeight)))
        {
            paneBitmap.Render(paneWindow);
            paneBitmap.Save(Path.Combine(output, "diff-pane.png"), PngBitmapEncoderOptions.Default);
        }

        pane.CanClose = false;
        Dispatcher.UIThread.RunJobs();
        Check(!closeMark.IsVisible, "a pane hosted by a dialog hides its close mark");
        paneWindow.Close();

        // The log page hosts the same pane in a real column. Only a render shows whether that column
        // actually opens next to the file tree, which is the whole point of not opening a window.
        var logViewModel = new GitClientViewModel(null!) { InlineDiff = presentation, IsInlineDiffOpen = true };
        var logView = new GitLogView(logViewModel);
        var logWindow = new Window { Width = 1400, Height = 800, Content = logView };
        logWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var inlinePane = logView.GetVisualDescendants().OfType<GitDiffPane>().Single();
        Check(inlinePane.Bounds.Width > 240, $"the log page gives the diff a column ({inlinePane.Bounds.Width:F0}px)");
        var paneOrigin = inlinePane.TranslatePoint(new Point(0, 0), logView) ?? new Point(0, 0);
        Check(paneOrigin.X > 590, $"the diff sits to the right of the three-pane layout (x={paneOrigin.X:F0})");
        using (var logBitmap = new RenderTargetBitmap(new PixelSize(1400, 800)))
        {
            logBitmap.Render(logWindow);
            logBitmap.Save(Path.Combine(output, "diff-log-page.png"), PngBitmapEncoderOptions.Default);
        }

        logViewModel.CloseInlineDiffCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        // A hidden control keeps its last arranged bounds, so the column itself is what proves the
        // layout went back to three panes.
        var layoutRoot = logView.FindControl<Grid>("LogLayoutRoot")!;
        Check(layoutRoot.ColumnDefinitions[6].Width.Value == 0, "closing the diff collapses its column again");
        logWindow.Close();

        // A patch the size of the server's 200 KB cap, to see whether the editor stays usable.
        var bigPatch = BuildBigPatch(400);
        var stopwatch = Stopwatch.StartNew();
        var bigDocument = DiffDocument.Parse(bigPatch);
        var parseMilliseconds = stopwatch.ElapsedMilliseconds;
        Check(!bigDocument.IsIncomplete, "the synthetic patch is complete");
        Check(bigDocument.Files[0].Hunks.Count == 400, "every hunk of the large patch is parsed");

        stopwatch.Restart();
        var (bigWindow, bigEditor) = CreateWindow(new DiffView { Document = bigDocument }, surfaces.Values.First());
        bigWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var loadMilliseconds = stopwatch.ElapsedMilliseconds;
        Check(bigEditor.Document.LineCount > 10_000, "the large patch becomes a ten-thousand-line document");
        Check(bigEditor.TextArea.TextView is { VisualLines.Count: > 0 }, "the large patch renders visible lines");
        Check(parseMilliseconds < 1500, $"parsing stays fast ({parseMilliseconds} ms)");
        Check(loadMilliseconds < 3000, $"loading stays fast ({loadMilliseconds} ms)");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(ViewWidth, ViewHeight)))
        {
            bitmap.Render(bigWindow);
            bitmap.Save(Path.Combine(output, "diff-large.png"), PngBitmapEncoderOptions.Default);
        }

        bigWindow.Close();
        Console.WriteLine($"PASS: {checks} diff render checks. {bigEditor.Document.LineCount} lines parsed in {parseMilliseconds} ms, shown in {loadMilliseconds} ms. Screenshots: {output}");
    }

    /// <summary>
    /// Publishes the service provider <c>LocalizedText</c> reads through, the same way the other
    /// client checks do: a presentation that formats captions cannot be built without it.
    /// </summary>
    private static ServiceProvider BuildLocalization()
    {
        var appearance = new AppearanceService(Application.Current!, new SystemStyleRegistry());
        var settings = new ShellSettings(appearance);
        var localization = new LocalizationService(settings, new SshDesktopSession(null!, new ShellSettings(null!)));
        var provider = new ServiceCollection().AddSingleton(localization).BuildServiceProvider();
        typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, provider);
        return provider;
    }

    /// <summary>
    /// Publishes the colour tokens the same way the application does. The diff view reads them from
    /// its resource chain, so without this it would render the fallback literals instead of the
    /// palette under test.
    /// </summary>
    private static Dictionary<string, string> ApplyPalette(ThemeVariant variant)
    {
        var colors = ThemePaletteDefaults.Resolve(AppearancePreferencesDto.Default, variant == ThemeVariant.Dark);
        var dictionary = new ResourceDictionary();
        foreach (var (name, value) in colors)
            dictionary[name + "Color"] = Color.Parse(value);

        var application = Application.Current!;
        application.RequestedThemeVariant = variant;
        application.Resources.MergedDictionaries.Clear();
        application.Resources.MergedDictionaries.Add(dictionary);
        return colors;
    }

    private static (Window Window, TextEditor Editor) CreateWindow(DiffView view, Color surface)
    {
        var window = new Window
        {
            Width = ViewWidth,
            Height = ViewHeight,
            Content = view,
            Background = new SolidColorBrush(surface),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view.GetVisualDescendants().OfType<TextEditor>().Single());
    }

    private static int RowOf(IReadOnlyList<DiffLine> rows, DiffLineKind kind)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Kind == kind)
                return index + 1;
        }

        throw new InvalidOperationException($"The sample patch has no {kind} line.");
    }

    private static PixelRegion RowBand(Snapshot snapshot, TextView textView, int lineNumber, double lineHeight)
    {
        var top = textView.GetVisualTopByDocumentLine(lineNumber) - textView.VerticalOffset;
        if (top < 0 || top + lineHeight > snapshot.Height)
            throw new InvalidOperationException($"Row {lineNumber} is outside the viewport at y={top:F0}.");
        return snapshot.Rect(0, (int)top, snapshot.Width, (int)lineHeight);
    }

    private static bool IsGreenish(Color color) => color.G > color.R + 8 && color.G > color.B + 4;

    private static bool IsReddish(Color color) => color.R > color.G + 12 && color.R > color.B + 8;

    private static double Luminance(Color color) => (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);

    private static int Distance(Color left, Color right)
        => Math.Max(Math.Abs(left.R - right.R), Math.Max(Math.Abs(left.G - right.G), Math.Abs(left.B - right.B)));

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

/// <summary>A bitmap held as BGRA bytes, so a check can ask questions about regions of it.</summary>
internal sealed class Snapshot
{
    private readonly byte[] _pixels;

    public Snapshot(RenderTargetBitmap bitmap, int width, int height)
    {
        Width = width;
        Height = height;
        var stride = width * 4;
        var buffer = Marshal.AllocHGlobal(stride * height);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, stride * height, stride);
            _pixels = new byte[stride * height];
            Marshal.Copy(buffer, _pixels, 0, _pixels.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public int Width { get; }

    public int Height { get; }

    public Color At(int x, int y)
    {
        var index = ((y * Width) + x) * 4;
        return Color.FromArgb(_pixels[index + 3], _pixels[index + 2], _pixels[index + 1], _pixels[index]);
    }

    public PixelRegion Rect(int x, int y, int width, int height)
        => new(this, x, y, Math.Min(width, Width - x), Math.Min(height, Height - y));

    public int Count(Func<Color, bool> predicate) => Rect(0, 0, Width, Height).Count(predicate);
}

/// <summary>A rectangular window onto a <see cref="Snapshot"/>.</summary>
internal sealed class PixelRegion(Snapshot snapshot, int x, int y, int width, int height)
{
    public int Count(Func<Color, bool> predicate)
    {
        var count = 0;
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                if (predicate(snapshot.At(column, row)))
                    count++;
            }
        }

        return count;
    }

    public double Fraction(Func<Color, bool> predicate)
    {
        var total = width * height;
        return total == 0 ? 0 : (double)Count(predicate) / total;
    }

    public PixelRegion Sub(int offsetX, int subWidth) => new(snapshot, x + offsetX, y, Math.Min(subWidth, width - offsetX), height);
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
