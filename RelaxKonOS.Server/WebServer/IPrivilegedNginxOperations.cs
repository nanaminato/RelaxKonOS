using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.WebServer;

/// <summary>Closed Nginx package and system-service operations; no executable or argument API.</summary>
public interface IPrivilegedNginxOperations
{
    Task<PrivilegedOperationResult> ApplySystemServiceActionAsync(NginxSystemServiceAction action, CancellationToken cancellationToken = default);
    Task<PrivilegedOperationResult> InstallPackageAsync(string? version, CancellationToken cancellationToken = default);
    Task<PrivilegedOperationResult> UninstallPackageAsync(CancellationToken cancellationToken = default);
    Task<PrivilegedOperationResult> WriteManagedFileAsync(string path, byte[] content, CancellationToken cancellationToken = default);
    Task<PrivilegedOperationResult> MoveManagedFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<PrivilegedOperationResult> DeleteManagedFileAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class PrivilegedNginxOperations(RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport transport) : IPrivilegedNginxOperations
{
    public Task<PrivilegedOperationResult> ApplySystemServiceActionAsync(NginxSystemServiceAction action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxSystemServiceAction, NginxServiceAction: action), cancellationToken);
    public Task<PrivilegedOperationResult> InstallPackageAsync(string? version, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxPackageInstall, PackageVersion: version), cancellationToken);
    public Task<PrivilegedOperationResult> UninstallPackageAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxPackageUninstall), cancellationToken);
    public Task<PrivilegedOperationResult> WriteManagedFileAsync(string path, byte[] content, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxWriteManagedFile, Path: path, ContentBase64: Convert.ToBase64String(content)), cancellationToken);
    public Task<PrivilegedOperationResult> MoveManagedFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxMoveManagedFile, Path: sourcePath, DestinationPath: destinationPath, Overwrite: overwrite), cancellationToken);
    public Task<PrivilegedOperationResult> DeleteManagedFileAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NginxDeleteManagedFile, Path: path), cancellationToken);

    private Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken) =>
        transport.ExecuteAsync(request, cancellationToken);
}
