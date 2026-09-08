using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Services.DesktopRestore;

/// <summary>
/// Runs desktop-state restoration once, after the shell's window host is ready. Individual
/// participant failures are deliberately isolated so an unavailable optional service cannot
/// block entry to the desktop.
/// </summary>
public sealed class DesktopRestoreOrchestrator
{
    private readonly IAuthSession _session;
    private readonly IReadOnlyList<IDesktopRestoreParticipant> _participants;
    private int _started;

    public DesktopRestoreOrchestrator(
        IAuthSession session,
        IEnumerable<IDesktopRestoreParticipant> participants)
    {
        _session = session;
        _participants = participants.OrderBy(participant => participant.Order).ToArray();
    }

    /// <summary>
    /// Restores the authenticated workspace once. Calling before authentication is a no-op and
    /// does not consume the one restore opportunity.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_session.State != AuthSessionState.Authenticated
            || Interlocked.Exchange(ref _started, 1) != 0)
            return;

        var context = new DesktopRestoreContext(Guid.NewGuid(), _session);
        foreach (var participant in _participants)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                await participant.RestoreAsync(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // State restoration is best effort. A participant may independently expose a
                // diagnostic surface; the desktop itself must remain available.
            }
        }
    }
}
