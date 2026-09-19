using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Settings;

public interface IHostIdentityService
{
    Task<HostIdentityState> ReadAsync(CancellationToken cancellationToken);
    Task<PrivilegedOperationResult> ApplyAsync(HostnameChange change, string expectedRevision, Guid operationId, CancellationToken cancellationToken);
}

public sealed class HostIdentityService(IPrivilegedOperationTransport transport) : IHostIdentityService
{
    public async Task<HostIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.HostIdentityRead, OperationId: Guid.NewGuid()), cancellationToken);
        if (!result.Success || result.HostIdentity is null)
            throw new SettingsException(503, "settings.identity." + result.ProblemCode.ToString().ToLowerInvariant());
        return result.HostIdentity;
    }

    public Task<PrivilegedOperationResult> ApplyAsync(HostnameChange change, string expectedRevision, Guid operationId, CancellationToken cancellationToken)
        => transport.ExecuteAsync(new(PrivilegedOperationKind.HostIdentityApply, HostName: change.HostName,
            ExpectedRevision: expectedRevision, OperationId: operationId), cancellationToken);
}
