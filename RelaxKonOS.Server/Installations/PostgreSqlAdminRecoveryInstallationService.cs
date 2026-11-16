using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Privileged;
namespace RelaxKonOS.Server.Installations;

public sealed class PostgreSqlAdminRecoveryInstallationService(IPrivilegedOperationTransport helper, PostgreSqlComponentAdapter component)
    : InstallationService<PostgreSqlInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.PostgreSqlAdminRecovery;
    public override IReadOnlyList<string> Resources => ["service:PostgreSql", "linux:apt-dpkg"];
    protected override bool Confirmed(PostgreSqlInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Configure or InstallationOperationKind.Repair;
    protected override void ValidateRequest(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string key)
    {
        if (request.Maintenance is not null || request.RemoteAccess is not null || request.Command is not null || request.FrozenPackage is not null || request.AccountCommand is not null
            || (kind == InstallationOperationKind.Configure ? request.AdminRecovery is not { InstanceId: var instance, ExpectedRevision: > 0 } || instance == Guid.Empty || request.RecoverOperationId is not null
                : request.AdminRecovery is not null || request.RecoverOperationId is not { } id || id == Guid.Empty))
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        var before = await component.ReadAsync(ct);
        if (request.RecoverOperationId is not null && before.PendingOperationId is not null && before.PendingAdminRecovery is null)
            throw new InstallationException("database.admin_recovery_authorization", 403);
        await progress.ReportAsync(new(InstallationStage.Configuring, Cancellable: false), ct);
        var original = request.RecoverOperationId ?? progress.OperationId;
        await helper.ExecuteAsync(new(request.RecoverOperationId is null ? PrivilegedOperationKind.PostgreSqlExecute : PrivilegedOperationKind.PostgreSqlRecover,
            OperationId: progress.OperationId, PostgreSql: request), CancellationToken.None);
        var state = await component.ReadAsync(CancellationToken.None, original);
        if (state.CommittedOperationId != original || state.PendingOperationId is not null) throw new InstallationInterruptedException("database.admin_recovery_pending");
        await progress.ReportAsync(new(InstallationStage.HealthChecking, Cancellable: false), CancellationToken.None);
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        try
        {
            var state = await component.ReadAsync(ct, operation.OperationId);
            return state.CommittedOperationId == operation.OperationId && state.PendingOperationId is null ? new(InstallationOperationState.Succeeded)
                : new(InstallationOperationState.Interrupted, "database.admin_recovery_pending");
        }
        catch (ComponentLifecycleException) { return new(InstallationOperationState.Interrupted, "database.admin_recovery_pending"); }
    }
}
