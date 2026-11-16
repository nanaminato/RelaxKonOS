using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Installations;

public sealed class PostgreSqlInstallationService(IPrivilegedOperationTransport helper, PostgreSqlComponentAdapter component)
    : InstallationService<PostgreSqlInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.PostgreSql;
    protected override bool Confirmed(PostgreSqlInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install or InstallationOperationKind.Configure or InstallationOperationKind.Repair;
    protected override void ValidateRequest(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string key)
    {
        if (request.Maintenance is not null || request.RemoteAccess is not null || request.AdminRecovery is not null || request.AccountCommand is not null) throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        var valid = kind switch
        {
            InstallationOperationKind.Install => request.FrozenPackage is { } frozen && frozen.ComponentId == component.ComponentId
                && PostgreSqlComponentAdapter.Valid(frozen.Request) && frozen.Request.IdempotencyKey == key
                && frozen.Expected?.Package is not null && request.Command is null && request.RecoverOperationId is null,
            InstallationOperationKind.Configure => request.Command is { } command && Enum.IsDefined(command.Action)
                && command.InstanceId != Guid.Empty && command.ExpectedRevision > 0 && command.Port is >= 1024 and <= 65535
                && request.FrozenPackage is null && request.RecoverOperationId is null,
            InstallationOperationKind.Repair => request.RecoverOperationId is { } id && id != Guid.Empty
                && request.Command is null && request.FrozenPackage is null,
            _ => false
        };
        if (!valid) throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string actor,
        IInstallationProgress progress, CancellationToken ct)
    {
        if (request.RecoverOperationId is not null && (await component.ReadAsync(ct)) is var pending && (pending.PendingAccount is not null || pending.PendingAdminRecovery is not null || pending.PendingRemoteAccess is not null || pending.PendingMaintenance is not null))
            throw new InstallationException("database.accounts_authorization_required", 403);
        await progress.ReportAsync(new(kind == InstallationOperationKind.Install ? InstallationStage.Installing : InstallationStage.Configuring, Cancellable: false), ct);
        var operation = request.RecoverOperationId ?? progress.OperationId;
        var result = await helper.ExecuteAsync(new(request.RecoverOperationId is null ? PrivilegedOperationKind.PostgreSqlExecute : PrivilegedOperationKind.PostgreSqlRecover,
            OperationId: progress.OperationId, PostgreSql: request), CancellationToken.None);
        // The native operation might have committed before its response was lost. Always ask the original receipt.
        PostgreSqlState state;
        try { state = await component.ReadAsync(CancellationToken.None, operation); }
        catch (ComponentLifecycleException) { throw new InstallationInterruptedException("database.recovery_required"); }
        if (state.PendingOperationId is not null || state.CommittedOperationId != operation)
        {
            if (state.PendingOperationId is not null || !result.Success) throw new InstallationInterruptedException("database.recovery_required");
            throw new InstallationException("database.operation_unverified");
        }
        await progress.ReportAsync(new(InstallationStage.HealthChecking, Cancellable: false), CancellationToken.None);
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        try
        {
            var state = await component.ReadAsync(ct, operation.OperationId);
            return state.CommittedOperationId == operation.OperationId && state.PendingOperationId is null
                ? new(InstallationOperationState.Succeeded) : new(InstallationOperationState.Interrupted, "database.recovery_required");
        }
        catch (ComponentLifecycleException) { return new(InstallationOperationState.Interrupted, "database.recovery_required"); }
    }
}
