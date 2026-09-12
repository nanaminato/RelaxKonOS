using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Settings;

public interface IHostTimeService
{
    Task<HostTimeState> ReadAsync(CancellationToken cancellationToken);
    Task<PrivilegedOperationResult> ApplyAsync(TimeZoneChange change, string expectedRevision, Guid operationId, CancellationToken cancellationToken);
}

public sealed class HostTimeService(IPrivilegedOperationTransport transport) : IHostTimeService
{
    public async Task<HostTimeState> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.HostTimeRead, OperationId: Guid.NewGuid()), cancellationToken);
        if (!result.Success || result.HostTime is null)
            throw new SettingsException(503, "settings.time." + result.ProblemCode.ToString().ToLowerInvariant());
        return result.HostTime;
    }

    public Task<PrivilegedOperationResult> ApplyAsync(TimeZoneChange change, string expectedRevision, Guid operationId, CancellationToken cancellationToken)
        => transport.ExecuteAsync(new(PrivilegedOperationKind.HostTimeApply, TimeZoneId: change.TimeZoneId,
            ExpectedRevision: expectedRevision, OperationId: operationId), cancellationToken);
}

public sealed class SettingsException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
