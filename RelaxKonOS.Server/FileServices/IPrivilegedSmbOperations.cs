using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.FileServices;

public interface IPrivilegedSmbOperations
{
    Task<PrivilegedOperationResult> DetectAsync(Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> InstallAsync(Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> ServiceAsync(SmbServiceAction action, Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> ReadManagedConfigurationAsync(Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> ReadUsersAsync(Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> ApplyLinuxConfigurationAsync(IReadOnlyList<SmbManagedShareRequest> shares, Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> ApplyWindowsShareAsync(SmbManagedShareRequest share, string? snapshot, Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> RemoveWindowsShareAsync(string id, string? snapshot, Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> SetWindowsServerSecurityAsync(string? snapshot, Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> SetUserEnabledAsync(string username, bool enabled, Guid operationId, CancellationToken ct);
    Task<PrivilegedOperationResult> SetUserPasswordAsync(string username, string password, Guid operationId, CancellationToken ct);
}

public sealed class PrivilegedSmbOperations(RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport transport) : IPrivilegedSmbOperations
{
    private Task<PrivilegedOperationResult> Run(PrivilegedOperationRequest request, CancellationToken ct) => transport.ExecuteAsync(request, ct);
    public Task<PrivilegedOperationResult> DetectAsync(Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbDetect, OperationId: id), ct);
    public Task<PrivilegedOperationResult> InstallAsync(Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbPackageInstall, OperationId: id), ct);
    public Task<PrivilegedOperationResult> ServiceAsync(SmbServiceAction action, Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbServiceAction, SmbServiceAction: action, OperationId: id), ct);
    public Task<PrivilegedOperationResult> ReadManagedConfigurationAsync(Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbReadManagedConfiguration, OperationId: id), ct);
    public Task<PrivilegedOperationResult> ReadUsersAsync(Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbReadUsers, OperationId: id), ct);
    public Task<PrivilegedOperationResult> ApplyLinuxConfigurationAsync(IReadOnlyList<SmbManagedShareRequest> shares, Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbApplyManagedConfiguration, SmbShares: shares, OperationId: id), ct);
    public Task<PrivilegedOperationResult> ApplyWindowsShareAsync(SmbManagedShareRequest share, string? snapshot, Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbApplyWindowsShare, SmbShare: share, SmbExpectedSnapshot: snapshot, OperationId: id), ct);
    public Task<PrivilegedOperationResult> RemoveWindowsShareAsync(string id, string? snapshot, Guid op, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbRemoveWindowsShare, SmbShareId: id, SmbExpectedSnapshot: snapshot, OperationId: op), ct);
    public Task<PrivilegedOperationResult> SetWindowsServerSecurityAsync(string? snapshot, Guid op, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbSetWindowsServerSecurity, SmbExpectedSnapshot: snapshot, OperationId: op), ct);
    public Task<PrivilegedOperationResult> SetUserEnabledAsync(string username, bool enabled, Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbSetUserEnabled, SmbUsername: username, SmbUserEnabled: enabled, OperationId: id), ct);
    public Task<PrivilegedOperationResult> SetUserPasswordAsync(string username, string password, Guid id, CancellationToken ct) => Run(new(PrivilegedOperationKind.SmbSetUserPassword, SmbUsername: username, SmbPassword: password, OperationId: id), ct);
}
