using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Uses the existing authenticated pipe; missing Helper never starts a UAC process.</summary>
public sealed class WindowsManagedRuntimeOperations(IPrivilegedOperationTransport transport)
{
    public Task<PrivilegedOperationResult> ExecuteAsync(WindowsManagedRuntimeRequest request, CancellationToken ct = default)
        => transport.ExecuteAsync(new(PrivilegedOperationKind.WindowsManagedRuntime, WindowsRuntime: request), ct);

    public static string Problem(PrivilegedOperationResult result) => result.ProblemCode switch
    {
        PrivilegedProblemCode.HelperUnavailable or PrivilegedProblemCode.TimedOut => "privileged-helper-unavailable",
        PrivilegedProblemCode.ResourceNotAllowed or PrivilegedProblemCode.AccessDenied => "tunnel.runtime_helper_policy_denied",
        _ => "tunnel.runtime_start_failed",
    };
}
