using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.UI.Themes;
using RelaxKonOS.WindowManager;
using CoreRect = RelaxKonOS.Core.Primitives.Rect;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

/// <summary>
/// Renders the real "File operations" window through the real window manager, so the title bar,
/// the 520x400 default size is verified with the dialog content instead of a
/// bare UserControl.
/// </summary>
internal static class FileOperationWindowChecks
{
    public static void Run(ShellSettings settings)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "file-operations");
        Directory.CreateDirectory(output);
        foreach (var dark in new[] { false, true })
        foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            settings.SystemStyleId = SystemStyleIds.WindowsLike;
            settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
            settings.Language = language;
            using (var fixture = new Fixture(language))
                fixture.Render(Path.Combine(output, $"{(dark ? "dark" : "light")}-{language}.png"), 520, 400);
            Console.WriteLine($"PASS: File operations window renders in {language} ({(dark ? "dark" : "light")}).");
        }

        settings.SystemStyleId = SystemStyleIds.WindowsLike;
        settings.Appearance = settings.Appearance with { Mode = ThemeKind.Light };
        settings.Language = "zh-CN";
        using (var fixture = new Fixture("zh-CN"))
        {
            fixture.ShowConflict();
            fixture.Render(Path.Combine(output, "light-zh-CN-conflict.png"), 520, 400);
            fixture.ShowMultiple();
            fixture.Render(Path.Combine(output, "light-zh-CN-multiple.png"), 520, 400);
            fixture.ShowCompactConflict();
            fixture.Render(Path.Combine(output, "light-zh-CN-multiple-conflict.png"), 520, 400);
        }
        Console.WriteLine("PASS: Conflict decisions and several transfers render inside the default window.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Window _host;
        private readonly WindowManagerService _manager;
        private readonly ManagedWindow _window;
        private readonly ExplorerOperationCenter _center = new(null!);
        private readonly ExplorerOperationCard _card;
        private readonly ExplorerOperationCard _second;

        public Fixture(string language)
        {
            var snapshot = new FileOperationDto(Guid.NewGuid(), FileOperationKind.Copy, FileOperationState.Running,
                [new(@"C:\Users\Alice\Videos\Holiday.mp4", "/home/alice/Desktop/Holiday.mp4"),
                 new(@"C:\Users\Alice\Videos\Scenes.mp4", "/home/alice/Desktop/Scenes.mp4")],
                @"C:\Users\Alice\Videos\Holiday.mp4", 48L * 1024 * 1024, 100L * 1024 * 1024, 1, 0, [], null, [], false,
                DateTimeOffset.UtcNow);
            _card = new ExplorerOperationCard(_center, snapshot) { IsUpload = true };
            SeedSpeed(_card);
            _second = new ExplorerOperationCard(_center, snapshot with
            {
                Id = Guid.NewGuid(),
                Kind = FileOperationKind.Move,
                Items = [new("/home/alice/Documents/Notes.txt", "/home/alice/Desktop/Notes.txt")],
                CurrentPath = "/home/alice/Documents/Notes.txt",
                CurrentBytes = 20L * 1024 * 1024,
                ProcessedItems = 2,
            }) { DetailsExpanded = false };
            _center.Jobs.Add(_card);

            var view = new ExplorerOperationsView { DataContext = _center };
            var canvas = new Canvas();
            _host = new Window { Width = 640, Height = 560, Content = canvas, Background = ThemeResources.Brush("ShellBackgroundBrush") };
            _manager = new WindowManagerService();
            _host.Show();
            Pump(); _manager.Attach(canvas); _manager.SetHostBounds(new CoreRect(0, 0, 640, 560));
            // Same bounds as ExplorerApp.ConfigureOperationsWindow, so the preview is the real dialog.
            _window = _manager.Create(new WindowCreateOptions(new AppId("relaxkonos.explorer"),
                LocalizedText.Get("explorer.operations.title"), view, new CoreRect(20, 20, 520, 400), IconGlyph: "📁"));
            View = view;
            Pump(); _host.UpdateLayout(); Pump();
        }

        public ExplorerOperationsView View { get; }

        public void ShowConflict() => _card.Snapshot = _card.Snapshot with
        {
            State = FileOperationState.WaitingForDecision,
            Issue = new(Guid.NewGuid(), "conflict", _card.Snapshot.CurrentPath!, _card.Snapshot.Items[0].DestinationPath,
                "", [FileOperationDecision.Replace, FileOperationDecision.KeepBoth, FileOperationDecision.Skip]),
        };

        public void ShowMultiple()
        {
            _card.Snapshot = _card.Snapshot with { State = FileOperationState.Running, Issue = null };
            SeedSpeed(_card);
            if (!_center.Jobs.Contains(_second)) _center.Jobs.Add(_second);
            _center.RefreshLayout();
        }

        /// <summary>A decision inside a compact row must still keep its actions reachable.</summary>
        public void ShowCompactConflict()
        {
            _second.Snapshot = _second.Snapshot with
            {
                State = FileOperationState.WaitingForDecision,
                Issue = new(Guid.NewGuid(), "conflict", _second.Snapshot.CurrentPath!,
                    _second.Snapshot.Items[0].DestinationPath, "", [FileOperationDecision.Replace, FileOperationDecision.Skip]),
            };
        }

        public void Render(string path, int width, int height)
        {
            Pump(); _host.UpdateLayout(); Pump();
            Check(_window.Info.Bounds.Width == width && _window.Info.Bounds.Height == height,
                "File operations opened at an unexpected size.");
            Check(_window.Info.Title == LocalizedText.Get("explorer.operations.title"),
                "File operations window lost its localized title.");
            // A single task keeps the 20px phase heading; several tasks are compact rows whose
            // percentage takes that line instead.
            if (_center.HasMultiple)
            {
                Check(View.GetVisualDescendants().OfType<TextBlock>()
                        .Any(text => text.IsEffectivelyVisible && text.Text == _card.CompactProgress),
                    "A compact row must state its percentage.");
            }
            else
            {
                var heading = View.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(text => text.IsEffectivelyVisible && text.Text == _card.ProgressText);
                Check(heading is not null && Math.Abs(heading.FontSize - 20) < 0.01,
                    "The operation phase must be a 20px heading in the single-task card.");
            }
            var close = View.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("operation-close")).ToArray();
            Check(close.Length == _center.Jobs.Count && close.All(button => button.Bounds.Width == 30 && button.Bounds.Height == 30),
                "Every card's cancel affordance must keep its 30px hit target.");
            var bars = View.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Check(bars.Length >= 1 && bars.All(bar => Math.Abs(bar.Height - 4) < 0.01),
                "Progress bars must be the 4px Windows track.");
            Check(bars.All(bar => bar.CornerRadius.TopLeft == 2), "Progress bars must keep rounded caps.");
            // With one task the whole card - including a conflict's decisions - fits without
            // scrolling; several tasks are allowed to grow into the list.
            if (!_center.HasMultiple)
            {
                var scroll = View.GetVisualDescendants().OfType<ScrollViewer>().Single();
                var firstCard = View.GetVisualDescendants().OfType<Border>()
                    .First(border => border.Classes.Contains("operation-card"));
                var cardOrigin = firstCard.TranslatePoint(default, View)!.Value;
                Check(cardOrigin.Y >= 0 && cardOrigin.Y + firstCard.Bounds.Height <= scroll.Bounds.Height + 1,
                    "A single card must fit in the window without scrolling.");
                if (_card.Snapshot.Issue is not null)
                {
                    var replace = View.GetVisualDescendants().OfType<Button>()
                        .Single(button => ReferenceEquals(button.Command, _card.ReplaceCommand));
                    Check(replace.TranslatePoint(default, View) is { } decision
                        && decision.Y + replace.Bounds.Height <= scroll.Bounds.Height + 1,
                        "Conflict decisions must stay inside the default dialog without scrolling.");
                }
            }
            if (View.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("[missing:") == true))
                throw new Exception("File operations window has missing translations.");
            using var rendered = new RenderTargetBitmap(new PixelSize(640, 560));
            rendered.Render(_host);
            rendered.Save(path, new PngBitmapEncoderOptions());
        }

        public void Dispose()
        {
            _manager.Close(_window);
            _host.Close();
        }

        private static void SeedSpeed(ExplorerOperationCard card)
        {
            var metrics = (FileTransferMetrics)typeof(ExplorerOperationCard)
                .GetField("_metrics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card)!;
            var now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            int[] samples = [0, 3, 7, 11, 13, 15, 19, 23, 26, 30, 34, 39, 48];
            for (var i = 0; i < samples.Length; i++) metrics.Observe(samples[i] * 1024L * 1024, null, true, now - 6 + i * .5);
            card.RefreshMeasurements();
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Pump()
        {
            for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        }
    }
}
