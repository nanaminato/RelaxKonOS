using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Privileged;
namespace RelaxKonOS.Server.Installations;

internal sealed class PostgreSqlRemoteAccessInstallationService(IPrivilegedOperationTransport helper, PostgreSqlComponentAdapter component, ICertificateStore certificates)
    : InstallationService<PostgreSqlInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.PostgreSqlRemoteAccess;
    public override IReadOnlyList<string> Resources => ["service:PostgreSql", "linux:apt-dpkg"];
    protected override bool Confirmed(PostgreSqlInstallationRequest r) => r.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Configure or InstallationOperationKind.Repair;
    protected override void ValidateRequest(InstallationOperationKind kind, PostgreSqlInstallationRequest r, string key)
    {
        if(r.Maintenance is not null || r.Command is not null || r.FrozenPackage is not null || r.AccountCommand is not null || r.AdminRecovery is not null
            || (kind==InstallationOperationKind.Configure ? r.RemoteAccess is not {} c || !PostgreSqlRemoteValidation.Valid(c) || r.RecoverOperationId is not null
                : r.RemoteAccess is not null || r.RecoverOperationId is not {} id || id==Guid.Empty)) throw new InstallationException(InstallationProblemCodes.InvalidRequest,400);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, PostgreSqlInstallationRequest request, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        var before=await component.ReadAsync(ct);
        if(request.RecoverOperationId is not null && before.PendingOperationId is not null && before.PendingRemoteAccess is null) throw new InstallationException("database.remote_authorization",403);
        var command=request.RemoteAccess ?? before.PendingRemoteAccess;
        PostgreSqlTlsMaterial? material=null;
        if(command is { Enabled:true })
        {
            var metadata=await certificates.GetAsync(command.CertificateId!.Value,ct);
            var paths=await certificates.GetNginxPathsAsync(command.CertificateId.Value,ct);
            if(metadata is null || metadata.Status is CertificateStatus.Revoked or CertificateStatus.Expired || paths is null) throw new InstallationException("database.tls_invalid");
            material=new(await File.ReadAllTextAsync(paths.Value.FullChainPath,ct),await File.ReadAllTextAsync(paths.Value.PrivateKeyPath,ct));
            using var certificate=X509Certificate2.CreateFromPem(material.CertificatePem,material.PrivateKeyPem);
            if(certificate.GetCertHashString(HashAlgorithmName.SHA256)!=command.CertificateFingerprint?.ToUpperInvariant()) throw new InstallationException("database.tls_invalid");
        }
        await progress.ReportAsync(new(InstallationStage.Configuring,Cancellable:false),ct);
        var original=request.RecoverOperationId??progress.OperationId;
        await helper.ExecuteAsync(new(request.RecoverOperationId is null?PrivilegedOperationKind.PostgreSqlExecute:PrivilegedOperationKind.PostgreSqlRecover,
            OperationId:progress.OperationId,PostgreSql:request,PostgreSqlTls:material),CancellationToken.None);
        var after=await component.ReadAsync(CancellationToken.None,original);
        if(after.CommittedOperationId!=original || after.PendingOperationId is not null) throw new InstallationInterruptedException("database.remote_recovery_required");
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation,CancellationToken ct)
    {
        try { var state=await component.ReadAsync(ct,operation.OperationId);return state.CommittedOperationId==operation.OperationId && state.PendingOperationId is null
            ?new(InstallationOperationState.Succeeded):new(InstallationOperationState.Interrupted,"database.remote_recovery_required"); }
        catch(ComponentLifecycleException){return new(InstallationOperationState.Interrupted,"database.remote_recovery_required");}
    }
}
