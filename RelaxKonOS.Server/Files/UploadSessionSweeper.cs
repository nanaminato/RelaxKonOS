namespace RelaxKonOS.Server.Files;

/// <summary>
/// Removes upload sessions that outlived their lifetime, together with the staging files they own.
/// It works from the session index alone: a staging file is only ever deleted when its session is in the
/// index and the name parses back to that session id. A user who genuinely owns a file called
/// <c>.something.rkup</c> therefore never loses it to this sweep.
/// </summary>
public sealed class UploadSessionSweeper(
    UploadSessionService sessions,
    UploadSessionOptions options,
    ILogger<UploadSessionSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(options.SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
            try
            {
                var removed = await sessions.SweepAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("File upload sweep removed {Count} expired session(s).", removed);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception exception)
            {
                // A failed sweep is retried on the next tick. It must never take the host down.
                logger.LogWarning(exception, "File upload session sweep failed.");
            }
        }
    }
}
