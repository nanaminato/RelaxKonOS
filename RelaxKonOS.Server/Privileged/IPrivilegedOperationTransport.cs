using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>
/// The only Server-to-Helper boundary. Platform selection belongs here; endpoints and domain
/// services must never start sudo, PowerShell, sc.exe, systemctl, or an elevated executable.
/// </summary>
public interface IPrivilegedOperationTransport
{
    Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default);
}
