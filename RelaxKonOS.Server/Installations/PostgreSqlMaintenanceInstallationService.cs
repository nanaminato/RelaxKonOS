using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Privileged;
namespace RelaxKonOS.Server.Installations;

public sealed class PostgreSqlMaintenanceInstallationService(IPrivilegedOperationTransport helper, PostgreSqlComponentAdapter component, RelaxKonOS.Server.Databases.PostgreSqlCredentialStore credentials)
    : InstallationService<PostgreSqlInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.PostgreSqlMaintenance;
    public override IReadOnlyList<string> Resources => ["service:PostgreSql", "linux:apt-dpkg"];
    protected override bool Confirmed(PostgreSqlInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Configure or InstallationOperationKind.Repair;
    protected override void ValidateRequest(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string key)
    {
        if (request.RemoteAccess is not null || request.Command is not null || request.FrozenPackage is not null || request.AccountCommand is not null || request.AdminRecovery is not null
            || (kind == InstallationOperationKind.Configure ? request.Maintenance is not {} command || !PostgreSqlMaintenanceValidation.Valid(command) || request.RecoverOperationId is not null
                : request.Maintenance is not null || request.RecoverOperationId is not { } id || id == Guid.Empty))
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        var before = await component.ReadAsync(ct);
        if (request.RecoverOperationId is not null && before.PendingOperationId is not null && before.PendingMaintenance is null)
            throw new InstallationException("database.maintenance_authorization", 403);
        // A previous Core process may have stopped after native deletion but before secret cleanup.
        // Do not clear the native tombstone through reinstall until its server-owned references are gone.
        if(request.Maintenance?.Action==PostgreSqlMaintenanceAction.Reinstall&&before.DataDeleted)Cleanup(before);
        await progress.ReportAsync(new(InstallationStage.Configuring, Cancellable: false), ct);
        var original = request.RecoverOperationId ?? progress.OperationId;
        var result=await helper.ExecuteAsync(new(request.RecoverOperationId is null ? PrivilegedOperationKind.PostgreSqlExecute : PrivilegedOperationKind.PostgreSqlRecover,
            OperationId: progress.OperationId, PostgreSql: request), CancellationToken.None);
        PostgreSqlState state;
        try {state=await component.ReadAsync(CancellationToken.None,original);}
        catch(ComponentLifecycleException){throw new InstallationInterruptedException("database.maintenance_pending");}
        if(state.CommittedOperationId!=original||state.PendingOperationId is not null)
        {
            if(state.PendingOperationId is not null||result.Success)throw new InstallationInterruptedException("database.maintenance_pending");
            throw new InstallationException(result.Error is {} code&&code.StartsWith("database.",StringComparison.Ordinal)?code:"database.maintenance_pending");
        }
        try {Cleanup(state);}
        catch(Exception ex) when(ex is InstallationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {throw new InstallationInterruptedException("database.maintenance_pending");}
        await progress.ReportAsync(new(InstallationStage.HealthChecking, Cancellable: false), CancellationToken.None);
    }
    private void Cleanup(PostgreSqlState state)
    {
        if(state.DataDeleted && state.PackageRemoved && state.InstanceId is {} instance)credentials.DeleteInstance(instance);
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        try
        {
            var state = await component.ReadAsync(ct, operation.OperationId);
            if(state.CommittedOperationId==operation.OperationId&&state.PendingOperationId is null)Cleanup(state);
            return state.CommittedOperationId == operation.OperationId && state.PendingOperationId is null ? new(InstallationOperationState.Succeeded)
                : new(InstallationOperationState.Interrupted, "database.maintenance_pending");
        }
        catch (Exception ex) when(ex is ComponentLifecycleException or InstallationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new(InstallationOperationState.Interrupted, "database.maintenance_pending"); }
    }
}
