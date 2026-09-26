using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Files;

/// <summary>
/// Removes upload sessions that outlived their lifetime, together with the staging files they own.
/// It works from the session index alone: a staging file is only ever deleted when its session is in the
/// index and the name parses back to that session id. A user who genuinely owns a file called
/// <c>.something.rkup</c> therefore never loses it to this sweep.
/// </summary>
public sealed class UploadSessionSweeper(
    UploadSessionStore store,
    UploadSessionOptions options,
    UploadSessionConcurrency concurrency,
    LocalFileService direct,
    IUserExecutionTransport transport,
    IServerModeResolver mode,
    IServiceScopeFactory scopes,
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
                var removed = 0;
                foreach (var session in store.Snapshot())
                {
                    if (!session.IsExpired(options, DateTimeOffset.UtcNow)) continue;
                    if (!UploadSessionService.IsOwnedStagingPath(session))
                    {
                        logger.LogError("Refusing to sweep an upload session whose staging path is not attributable to its record. SessionId={SessionId}",
                            session.SessionId);
                        continue;
                    }
                    if (!await DeleteAsOwnerAsync(session, stoppingToken)) continue;
                    if (store.Remove(session.SessionId))
                    {
                        concurrency.SessionGates.TryRemove(session.SessionId, out _);
                        removed++;
                        logger.LogInformation("File upload session expired and was removed. SessionId={SessionId}, Bytes={Bytes}, CreatedAt={CreatedAt}",
                            session.SessionId, session.Offset, session.CreatedAt);
                    }
                }
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

    private async Task<bool> DeleteAsOwnerAsync(UploadSessionRecord session, CancellationToken cancellationToken)
    {
        if (mode.Mode == ServerMode.User) return direct.DeleteStagingFile(session.StagingPath);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(JwtRegisteredClaimNames.Sub, session.IdentityKey)], "upload-session-sweeper"));
            var identity = scope.ServiceProvider.GetRequiredService<IUserExecutionContextResolver>().Resolve(principal).Identity;
            var result = await transport.ExecuteAsync(new UserExecutionRequest(identity,
                UserExecutionOperationKind.FileDeleteStaging, Path: session.StagingPath, OperationId: Guid.NewGuid()),
                cancellationToken);
            if (result.Success || result.ProblemCode == UserExecutionProblemCode.NotFound) return true;
            logger.LogWarning("Upload staging cleanup was deferred. SessionId={SessionId} ProblemCode={ProblemCode}",
                session.SessionId, result.ProblemCode);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or TimeoutException or ArgumentException or UserExecutionException)
        {
            logger.LogWarning(exception, "Upload staging cleanup was deferred. SessionId={SessionId}", session.SessionId);
            return false;
        }
    }
}
