using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>User Mode has no Helper transport. This prevents an accidental future caller from spawning sudo.</summary>
public sealed class DisabledPrivilegedOperationTransport : IPrivilegedOperationTransport
{
    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new PrivilegedOperationResult(false, 69, Error: "privileged operations are disabled in User Mode",
            ProblemCode: PrivilegedProblemCode.HelperUnavailable));
}
