using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Tailscale;

/// <summary>Production status access goes through the authenticated, audited Helper transport.</summary>
public sealed class HelperTailscaleManager(IPrivilegedOperationTransport transport) : ITailscaleManager
{
    public async Task<TailscaleStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleStatus,
            OperationId: Guid.NewGuid()), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleStatus is { } status)
            return status;
        return result.ProblemCode == PrivilegedProblemCode.TimedOut
            ? new("timeout", ProblemCode: "tailscale.timeout")
            : new("unavailable", ProblemCode: "tailscale.helper_unavailable");
    }
}
