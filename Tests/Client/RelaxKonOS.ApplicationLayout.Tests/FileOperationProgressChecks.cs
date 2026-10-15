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
        var view = new ExplorerOperationsView { DataContext = center };
        host.Width = 580; host.Height = 490; host.Content = view;
        foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            settings.Language = language;
            card.RefreshMeasurements();
            Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            if (view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("[missing:") == true))
                throw new Exception("Progress UI has missing translations: " + language);
            using var bitmap = new RenderTargetBitmap(new PixelSize(580, 490));
            bitmap.Render(view); bitmap.Save(Path.Combine(output, "FileProgress-" + language + ".png"), PngBitmapEncoderOptions.Default);
        }
        settings.Language = "zh-CN";
        appearance.Apply(RelaxKonOS.Protocol.Desktop.ThemeKind.Dark, RelaxKonOS.Protocol.Workspace.AppearancePreferencesDto.Default, RelaxKonOS.Protocol.Workspace.SystemStyles.SystemStyleIds.WindowsLike);
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        using (var bitmap = new RenderTargetBitmap(new PixelSize(580, 490)))
        { bitmap.Render(view); bitmap.Save(Path.Combine(output, "FileProgress-dark.png"), PngBitmapEncoderOptions.Default); }
        appearance.Apply(RelaxKonOS.Protocol.Desktop.ThemeKind.Light, RelaxKonOS.Protocol.Workspace.AppearancePreferencesDto.Default, RelaxKonOS.Protocol.Workspace.SystemStyles.SystemStyleIds.WindowsLike);
        card.DetailsExpanded = false;
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        if (view.GetVisualDescendants().OfType<RelaxKonOS.Client.Apps.Explorer.Controls.TransferSpeedGraph>().Single().IsEffectivelyVisible)
            throw new Exception("Collapsing details must hide the speed graph.");
        card.DetailsExpanded = true;
        card.Snapshot = snapshot with { State = FileOperationState.WaitingForDecision,
            Issue = new(Guid.NewGuid(), "conflict", snapshot.CurrentPath!, snapshot.Items[0].DestinationPath,
                "", [FileOperationDecision.Replace, FileOperationDecision.KeepBoth, FileOperationDecision.Skip]) };
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        if (!view.GetVisualDescendants().OfType<CheckBox>().Single().IsEffectivelyVisible) throw new Exception("Conflicts must remain actionable.");
        var replace = view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, card.ReplaceCommand));
        if (replace.TranslatePoint(default, view) is not { } point || point.Y + replace.Bounds.Height > view.Bounds.Height)
            throw new Exception("Conflict actions must be visible without scrolling.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(580, 490)))
        { bitmap.Render(view); bitmap.Save(Path.Combine(output, "FileProgress-conflict.png"), PngBitmapEncoderOptions.Default); }
        Console.WriteLine("PASS: Windows-style file progress renders in three languages, collapses details and keeps conflict actions accessible.");
    }
}
