using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Structured SCM/systemd operations through the authenticated Helper; no local process fallback.</summary>
public sealed class ManagedRuntimeOperations(IPrivilegedOperationTransport transport)
{
    public Task<PrivilegedOperationResult> ExecuteAsync(ManagedRuntimeRequest request, CancellationToken ct = default)
        => transport.ExecuteAsync(new(PrivilegedOperationKind.ManagedRuntime, ManagedRuntime: request), ct);

    public static string Problem(PrivilegedOperationResult result) => result.ProblemCode switch
    {
        PrivilegedProblemCode.HelperUnavailable or PrivilegedProblemCode.TimedOut => "privileged-helper-unavailable",
        PrivilegedProblemCode.ResourceNotAllowed or PrivilegedProblemCode.AccessDenied => "tunnel.runtime_helper_policy_denied",
        _ => "tunnel.runtime_start_failed",
    };
}
