using System.Text.Json;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Installations;

public sealed class MySqlInstallationService(IPrivilegedOperationTransport helper)
    : InstallationService<MySqlRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.MySql;
    public override IReadOnlyList<string> Resources => ["service:MySql", "linux:apt-dpkg"];
    protected override bool Confirmed(MySqlRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) =>
        kind is InstallationOperationKind.Install or InstallationOperationKind.Configure or InstallationOperationKind.Repair;

    protected override void ValidateRequest(InstallationOperationKind kind, MySqlRequest request, string key)
    {
        if (!request.Valid || !(kind switch
        {
            InstallationOperationKind.Install => request.Action == MySqlAction.Install,
            InstallationOperationKind.Configure => request.Action is not null and not MySqlAction.Install,
            InstallationOperationKind.Repair => request.RecoverOperationId is not null,
            _ => false
        }))
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }

    public async Task<MySqlState> ReadAsync(CancellationToken ct, Guid? operation = null)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.MySqlQuery, OperationId: operation), ct);
        if (!result.Success || result.OutputBase64 is null)
            throw new InstallationException("database.state_unavailable", 503);
        return JsonSerializer.Deserialize<MySqlState>(Convert.FromBase64String(result.OutputBase64))
            ?? throw new InstallationException("database.state_unavailable", 503);
    }

    protected override async Task ExecuteAsync(InstallationOperationKind kind, MySqlRequest request, string actor,
        IInstallationProgress progress, CancellationToken ct)
    {
        await progress.ReportAsync(new(kind == InstallationOperationKind.Install ? InstallationStage.Installing : InstallationStage.Configuring,
            Cancellable: false), ct);
        var original = request.RecoverOperationId ?? progress.OperationId;
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.MySqlExecute,
            OperationId: progress.OperationId, MySql: request), CancellationToken.None);
        MySqlState state;
        try { state = await ReadAsync(CancellationToken.None, original); }
        catch (Exception error) when (error is InstallationException or JsonException or FormatException or IOException)
        { throw new InstallationInterruptedException("database.recovery_required"); }
        if (state.CommittedOperationId == original && state.PendingOperationId is null) return;
        if (state.PendingOperationId is not null || result.Success)
            throw new InstallationInterruptedException("database.recovery_required");
        throw new InstallationException(result.Error is { } code && code.StartsWith("database.", StringComparison.Ordinal)
            ? code : "database.operation_unavailable");
    }

    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        try
        {
            var state = await ReadAsync(ct, operation.OperationId);
            if (state.CommittedOperationId == operation.OperationId && state.PendingOperationId is null)
                return new(InstallationOperationState.Succeeded);
        }
        catch (Exception error) when (error is InstallationException or JsonException or FormatException or IOException) { }
        return new(InstallationOperationState.Interrupted, "database.recovery_required");
    }
}
