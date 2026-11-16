using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Databases;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Installations;

public sealed class PostgreSqlAccountsInstallationService(IPrivilegedOperationTransport helper, PostgreSqlComponentAdapter component,
    PostgreSqlCredentialStore credentials) : InstallationService<PostgreSqlInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.PostgreSqlAccounts;
    public override IReadOnlyList<string> Resources => ["service:PostgreSql", "linux:apt-dpkg"];
    protected override bool Confirmed(PostgreSqlInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Configure or InstallationOperationKind.Repair;
    protected override void ValidateRequest(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string key)
    {
        if (request.Maintenance is not null || request.RemoteAccess is not null || request.AdminRecovery is not null || request.Command is not null || request.FrozenPackage is not null
            || (kind == InstallationOperationKind.Configure ? request.AccountCommand is not { } command || !PostgreSqlAccountValidation.Valid(command) || request.RecoverOperationId is not null
                : request.AccountCommand is not null || request.RecoverOperationId is not { } id || id == Guid.Empty))
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        var before = await component.ReadAsync(ct);
        if (request.RecoverOperationId is not null && before.PendingOperationId is not null && before.PendingAccount is null)
            throw new InstallationException("database.accounts_authorization_required", 403);
        var command = request.AccountCommand ?? before.PendingAccount;
        PostgreSqlSecrets? secrets = null;
        if (command is not null)
        {
            var current = before.Accounts?.SingleOrDefault(x => x.Name == command.Account && x.Database == command.Database);
            var reference = command.CredentialReference ?? current?.CredentialReference;
            if (reference is { } value)
            {
                var password = credentials.Read(value, command.InstanceId, command.CredentialReference is not null ? actor : null);
                var old = command.Action == PostgreSqlAccountAction.ChangePassword && current is not null
                    ? credentials.Read(current.CredentialReference, command.InstanceId) : null;
                secrets = new(password, old);
            }
        }
        else if (request.RecoverOperationId is null) throw new InstallationException("database.request_invalid");
        await progress.ReportAsync(new(InstallationStage.Configuring, Cancellable: false), ct);
        var original = request.RecoverOperationId ?? progress.OperationId;
        await helper.ExecuteAsync(new(request.RecoverOperationId is null ? PrivilegedOperationKind.PostgreSqlExecute : PrivilegedOperationKind.PostgreSqlRecover,
            OperationId: progress.OperationId, PostgreSql: request, PostgreSqlSecrets: secrets), CancellationToken.None);
        var state = await component.ReadAsync(CancellationToken.None, original);
        if (state.CommittedOperationId != original || state.PendingOperationId is not null) throw new InstallationInterruptedException("database.accounts_recovery_required");
        await progress.ReportAsync(new(InstallationStage.HealthChecking, Cancellable: false), CancellationToken.None);
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        try
        {
            var state = await component.ReadAsync(ct, operation.OperationId);
            return state.CommittedOperationId == operation.OperationId && state.PendingOperationId is null ? new(InstallationOperationState.Succeeded)
                : new(InstallationOperationState.Interrupted, "database.accounts_recovery_required");
        }
        catch (ComponentLifecycleException) { return new(InstallationOperationState.Interrupted, "database.accounts_recovery_required"); }
    }
}
