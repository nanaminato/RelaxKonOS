using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Protocol.Files;

public static class FileTransferMetricsChecks
{
    public static void Run(Action<bool,string> check)
    {
        var metrics = new FileTransferMetrics();
        metrics.Observe(0, "a", true, 0);
        metrics.Observe(100, "a", true, .5);
        check(!metrics.IsReady, "Throughput waits for a meaningful measurement interval");
        metrics.Observe(200, "a", true, 1);
        check(metrics.IsReady && metrics.BytesPerSecond == 200, "Throughput is computed from measured bytes and elapsed time");
        for (var time = 2; time <= 7; time++) metrics.Observe(200, "a", true, time);
        check(metrics.BytesPerSecond == 0, "A stalled transfer decays to zero rather than retaining its previous speed");
        metrics.Observe(5, "b", true, 8);
        check(!metrics.IsReady && metrics.History.Count == 0, "Changing the current server file resets speed measurements");
        metrics.Observe(100, "b", true, 9);
        metrics.Observe(150, "b", false, 10);
        check(metrics.BytesPerSecond == 0 && !metrics.IsReady, "Waiting for a conflict clears speed and time estimates");
        metrics.Observe(150, "b", true, 20);
        metrics.Observe(350, "b", true, 21);
        check(metrics.BytesPerSecond == 200, "Resuming excludes time spent waiting for a decision");
        metrics.Observe(1, "b", true, 22);
        check(!metrics.IsReady && metrics.BytesPerSecond == 0, "A counter rewind never produces a negative transfer speed");
        for (var i = 0; i < 100; i++) metrics.Observe(i * 1000, "b", true, 23 + i);
        check(metrics.History.Count == 60, "Transfer graph history stays bounded");
        check(FileTransferMetrics.FormatBytes(1048576) == "1 MB", "Transfer sizes use readable units");
        var dto = new FileOperationDto(Guid.NewGuid(), FileOperationKind.Copy, FileOperationState.Running,
            [new("/source/long.bin", "/destination/long.bin")], "/source/back\\slash.bin", 50, 100, 0, 0, [], null, [], false, DateTimeOffset.UtcNow);
        var card = new ExplorerOperationCard(new(null!), dto);
        check(card.CurrentFileName == "back\\slash.bin", "POSIX filenames preserve backslashes in the progress display");
        check(card.RemainingTime.Contains("calculating") && card.ProgressScope.Contains("current_file_short"),
            "Current-file server counters never pretend to be an overall task time estimate");
        card.Snapshot = dto with { CurrentTotalBytes = 0, State = FileOperationState.Queued };
        check(card.ProgressText == card.StateText && !card.ShowSpeedGraph, "Unmeasured queued work shows its state without a fake percentage");
        card.Snapshot = dto with { State = FileOperationState.WaitingForDecision,
            Issue = new(Guid.NewGuid(), "conflict", "/a", "/b", "", [FileOperationDecision.Skip]) };
        check(!card.ShowSpeedGraph && !card.ShowTransferDetails, "A conflict puts decision actions ahead of transfer details");
    }
}
