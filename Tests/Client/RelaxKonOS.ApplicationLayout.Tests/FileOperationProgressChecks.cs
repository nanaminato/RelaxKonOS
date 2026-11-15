using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Files;

internal static class FileOperationProgressChecks
{
    public static void Run(Window host, ShellSettings settings, string output, RelaxKonOS.Client.Services.Theming.AppearanceService appearance)
    {
        var center = new ExplorerOperationCenter(null!);
        var snapshot = new FileOperationDto(Guid.NewGuid(), FileOperationKind.Copy, FileOperationState.Running,
            [new(@"C:\Users\Alice\Videos\Holiday.mp4", "/home/alice/Desktop/Holiday.mp4"), new(@"C:\Users\Alice\Videos\Scenes.mp4", "/home/alice/Desktop/Scenes.mp4")],
            @"C:\Users\Alice\Videos\Holiday.mp4", 48L * 1024 * 1024, 100L * 1024 * 1024, 1, 0, [], null, [], false, DateTimeOffset.UtcNow);
        var card = new ExplorerOperationCard(center, snapshot) { IsUpload = true };
        var metrics = (FileTransferMetrics)typeof(ExplorerOperationCard).GetField("_metrics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card)!;
        var now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        int[] samples = [0, 3, 7, 11, 13, 15, 19, 23, 26, 30, 34, 39, 48];
        for (var i = 0; i < samples.Length; i++) metrics.Observe((long)samples[i] * 1024 * 1024, null, true, now - 6 + i * .5);
        center.Jobs.Add(card);
        center.RefreshLayout();
        var view = new ExplorerOperationsView { DataContext = center };
        // The real window opens at 520x400; rendering at that size keeps the checks honest about
        // what a single-card dialog actually shows.
        host.Width = 520; host.Height = 400; host.Content = view;
        foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            settings.Language = language;
            card.RefreshMeasurements();
            Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            if (view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("[missing:") == true))
                throw new Exception("Progress UI has missing translations: " + language);
            using var bitmap = new RenderTargetBitmap(new PixelSize(520, 400));
            bitmap.Render(view); bitmap.Save(Path.Combine(output, "FileProgress-" + language + ".png"), PngBitmapEncoderOptions.Default);
        }
        settings.Language = "zh-CN";
        appearance.Apply(RelaxKonOS.Protocol.Desktop.ThemeKind.Dark, RelaxKonOS.Protocol.Workspace.AppearancePreferencesDto.Default, RelaxKonOS.Protocol.Workspace.SystemStyles.SystemStyleIds.WindowsLike);
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        using (var bitmap = new RenderTargetBitmap(new PixelSize(520, 400)))
        { bitmap.Render(view); bitmap.Save(Path.Combine(output, "FileProgress-dark.png"), PngBitmapEncoderOptions.Default); }
        appearance.Apply(RelaxKonOS.Protocol.Desktop.ThemeKind.Light, RelaxKonOS.Protocol.Workspace.AppearancePreferencesDto.Default, RelaxKonOS.Protocol.Workspace.SystemStyles.SystemStyleIds.WindowsLike);
        if (card.Compact) throw new Exception("A single task must keep the full card, not a compact row.");
        if (!view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == card.ProgressText && Math.Abs(text.FontSize - 20) < .01))
            throw new Exception("The single task must show its 20px phase heading.");
        card.DetailsExpanded = false;
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        if (view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == card.SourceLocation))
            throw new Exception("Collapsing details must hide the source/destination rows.");
        card.DetailsExpanded = true;
        card.Snapshot = snapshot with { State = FileOperationState.WaitingForDecision,
            Issue = new(Guid.NewGuid(), "conflict", snapshot.CurrentPath!, snapshot.Items[0].DestinationPath,
                "", [FileOperationDecision.Replace, FileOperationDecision.KeepBoth, FileOperationDecision.Skip]) };
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        if (!view.GetVisualDescendants().OfType<CheckBox>().Single().IsEffectivelyVisible) throw new Exception("Conflicts must remain actionable.");
        var replace = view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, card.ReplaceCommand));
        if (replace.TranslatePoint(default, view) is not { } point || point.Y + replace.Bounds.Height > view.Bounds.Height)
            throw new Exception("Conflict actions must be visible without scrolling.");
        if (Math.Abs(replace.HorizontalContentAlignment - Avalonia.Layout.HorizontalAlignment.Center) > 0.01)
            throw new Exception("Conflict actions must centre their labels.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(520, 400)))
        { bitmap.Render(view); bitmap.Save(Path.Combine(output, "FileProgress-conflict.png"), PngBitmapEncoderOptions.Default); }

        // A second transfer turns the window into Windows' action list: one compact row per task,
        // each keeping its own percentage, route and decision.
        card.Snapshot = snapshot;
        var second = new ExplorerOperationCard(center, snapshot with
        {
            Id = Guid.NewGuid(),
            Kind = FileOperationKind.Move,
            Items = [new("/home/alice/Documents/Notes.txt", "/home/alice/Desktop/Notes.txt")],
            CurrentPath = "/home/alice/Documents/Notes.txt",
            CurrentBytes = 20L * 1024 * 1024,
            ProcessedItems = 2,
        });
        center.Jobs.Add(second);
        center.RefreshLayout();
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        if (!center.HasMultiple) throw new Exception("Two transfers must switch the window to the action list.");
        if (!card.Compact || !second.Compact) throw new Exception("Every task must become a compact row when several run.");
        if (!view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == card.CompactProgress))
            throw new Exception("A compact row must state its percentage.");
        if (view.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && ReferenceEquals(button.Command, card.ToggleDetailsCommand)))
            throw new Exception("Compact rows must not offer per-row details.");
        if (view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("[missing:") == true))
            throw new Exception("The action list has missing translations.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(520, 400)))
        { bitmap.Render(view); bitmap.Save(Path.Combine(output, "FileProgress-multi.png"), PngBitmapEncoderOptions.Default); }

        Console.WriteLine("PASS: Windows-style file progress renders as a single card and as a compact action list, keeps conflict actions reachable and centres their labels.");
    }
}
