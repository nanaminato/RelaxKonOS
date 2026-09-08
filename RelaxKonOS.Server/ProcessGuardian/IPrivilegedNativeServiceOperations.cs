using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>Capability-specific service control boundary; it is not a general process runner.</summary>
public interface IPrivilegedNativeServiceOperations
{
    Task<PrivilegedOperationResult> ApplyAsync(string serviceId, PrivilegedServiceAction action, CancellationToken cancellationToken = default);
}

public sealed class PrivilegedNativeServiceOperations(RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport transport) : IPrivilegedNativeServiceOperations
{
    public Task<PrivilegedOperationResult> ApplyAsync(string serviceId, PrivilegedServiceAction action, CancellationToken cancellationToken = default)
        => transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NativeServiceAction,
            ServiceId: serviceId, ServiceAction: action), cancellationToken);
}
