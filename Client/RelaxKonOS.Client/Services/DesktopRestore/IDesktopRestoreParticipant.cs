using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Services.DesktopRestore;

/// <summary>
/// Restores one piece of runtime state after an authenticated desktop has attached its window host.
/// Implementations must be idempotent for a single desktop instance and must not make a failed
/// restore prevent the remainder of the desktop from becoming usable.
/// </summary>
public interface IDesktopRestoreParticipant
{
    /// <summary>Relative restore order. Lower values run first.</summary>
    int Order { get; }

    Task RestoreAsync(DesktopRestoreContext context, CancellationToken cancellationToken);
}

/// <summary>State shared by participants for one authenticated desktop instance.</summary>
public sealed record DesktopRestoreContext(Guid DesktopInstanceId, IAuthSession Session);
