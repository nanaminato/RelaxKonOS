using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.FileServices;

public interface ISambaPlatformAdapter
{
    Task<FileServiceStatusDto> DetectAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct);
    Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct);
    Task<IReadOnlyList<FileShareDto>> ReadManagedSharesAsync(CancellationToken ct);
    Task<IReadOnlyList<FileServiceUserDto>> ReadUsersAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> ApplySharesAsync(IReadOnlyList<FileShareDto> current, Guid operationId, CancellationToken ct);
    Task<FileServiceOperationResultDto> SetUserAsync(string username, bool enabled, string? password, Guid id, CancellationToken ct);
}

public interface IWindowsSmbPlatformAdapter
{
    Task<FileServiceStatusDto> DetectAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct);
    Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct);
    Task<IReadOnlyList<FileShareDto>> ReadManagedSharesAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> ApplyShareAsync(FileShareDto share, string? expectedSnapshot, Guid id, CancellationToken ct);
    Task<FileServiceOperationResultDto> RemoveShareAsync(string id, string? expectedSnapshot, Guid operationId, CancellationToken ct);
    Task<WindowsSmbSecurityOperationResult> ApplyServerSecurityAsync(string? expectedSnapshot, Guid operationId, CancellationToken ct);
}

public sealed record WindowsSmbSecurityOperationResult(FileServiceOperationResultDto Operation, string? SnapshotHash);

public sealed class LinuxSambaPlatformAdapter(IPrivilegedSmbOperations helper) : ISambaPlatformAdapter
{
    public async Task<FileServiceStatusDto> DetectAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return Unsupported();
        var result = await helper.DetectAsync(Guid.NewGuid(), ct);
        if (!result.Success) return new(FileServiceProtocol.Smb, result.ProblemCode == PrivilegedProblemCode.HelperUnavailable ? FileServiceRuntimeState.Unavailable : FileServiceRuntimeState.Unsupported,
            null, false, false, result.ProblemCode == PrivilegedProblemCode.UnsupportedOperation ? FileServiceProblemCodes.UnsupportedPlatform : Problem(result));
        return DecodeStatus(result) ?? new(FileServiceProtocol.Smb, FileServiceRuntimeState.NotInstalled, null, false, false, FileServiceProblemCodes.NotInstalled);
    }
    public async Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct) => Result(id, await helper.InstallAsync(id, ct));
    public async Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct) => Result(id,
        await helper.ServiceAsync(action switch { SmbLifecycleAction.Start => SmbServiceAction.Start, SmbLifecycleAction.Stop => SmbServiceAction.Stop, _ => SmbServiceAction.Restart }, id, ct));
    public async Task<IReadOnlyList<FileShareDto>> ReadManagedSharesAsync(CancellationToken ct)
    {
        var result = await helper.ReadManagedConfigurationAsync(Guid.NewGuid(), ct);
        return result.Success ? Decode<List<FileShareDto>>(result) ?? [] : [];
    }
    public async Task<IReadOnlyList<FileServiceUserDto>> ReadUsersAsync(CancellationToken ct)
    {
        var result = await helper.ReadUsersAsync(Guid.NewGuid(), ct);
        return result.Success ? Decode<List<FileServiceUserDto>>(result) ?? [] : [];
    }
    public async Task<FileServiceOperationResultDto> ApplySharesAsync(IReadOnlyList<FileShareDto> current, Guid operationId, CancellationToken ct)
    {
        var shares = current.Select(s => new SmbManagedShareRequest(s.Id, s.Name, s.Path, s.Description, s.ReadOnly, s.Enabled, s.GuestAllowed,
            s.Permissions.Select(p => new SmbSharePermissionRequest(p.Principal, p.Access.ToString())).ToArray())).ToArray();
        return Result(operationId, await helper.ApplyLinuxConfigurationAsync(shares, operationId, ct));
    }
    public async Task<FileServiceOperationResultDto> SetUserAsync(string username, bool enabled, string? password, Guid id, CancellationToken ct) => password is null
        ? Result(id, await helper.SetUserEnabledAsync(username, enabled, id, ct)) : Result(id, await helper.SetUserPasswordAsync(username, password, id, ct));
    private static FileServiceStatusDto Unsupported() => new(FileServiceProtocol.Smb, FileServiceRuntimeState.Unsupported, null, false, false, FileServiceProblemCodes.UnsupportedPlatform);
    internal static FileServiceOperationResultDto Result(Guid id, PrivilegedOperationResult result) => new(id, result.Success, result.Success ? null : Problem(result));
    internal static string Problem(PrivilegedOperationResult result)
    {
        // Helper errors are fixed, non-secret classifications. Preserve the product-level
        // distinction between a managed-config ownership refusal and a configuration failure.
        if (result.Error?.Contains("not safely managed", StringComparison.OrdinalIgnoreCase) == true
            || result.Error?.Contains("externally modified", StringComparison.OrdinalIgnoreCase) == true)
            return FileServiceProblemCodes.ConfigurationUnmanaged;
        if (result.Error?.Contains("validation failed", StringComparison.OrdinalIgnoreCase) == true
            || result.Error?.Contains("configuration invalid", StringComparison.OrdinalIgnoreCase) == true)
            return FileServiceProblemCodes.ConfigurationInvalid;
        if (result.Error?.Contains("port conflict", StringComparison.OrdinalIgnoreCase) == true)
            return FileServiceProblemCodes.PortInUse;
        if (result.Error?.Contains("port unavailable", StringComparison.OrdinalIgnoreCase) == true)
            return FileServiceProblemCodes.PortUnavailable;
        if (result.Error?.Contains("service health", StringComparison.OrdinalIgnoreCase) == true)
            return FileServiceProblemCodes.ServiceFailed;
        return result.ProblemCode switch
        {
            PrivilegedProblemCode.HelperUnavailable => FileServiceProblemCodes.HelperUnavailable,
            PrivilegedProblemCode.NotFound => FileServiceProblemCodes.NotInstalled,
            PrivilegedProblemCode.Conflict => FileServiceProblemCodes.ReconciliationRequired,
            _ => FileServiceProblemCodes.ConfigurationInvalid,
        };
    }
    internal static T? Decode<T>(PrivilegedOperationResult result)
    {
        try { return result.OutputBase64 is null ? default : JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64)); }
        catch (JsonException) { return default; }
    }
    internal static FileServiceStatusDto? DecodeStatus(PrivilegedOperationResult result) => Decode<FileServiceStatusDto>(result);
}

/// <summary>Windows requests go through the authenticated LocalSystem pipe. No PowerShell or command execution exists here.</summary>
public sealed class WindowsSmbPlatformAdapter(IPrivilegedSmbOperations helper) : IWindowsSmbPlatformAdapter
{
    public async Task<FileServiceStatusDto> DetectAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new(FileServiceProtocol.Smb, FileServiceRuntimeState.Unsupported, null, false, false, FileServiceProblemCodes.UnsupportedPlatform);
        var result = await helper.DetectAsync(Guid.NewGuid(), ct);
        return result.Success ? LinuxSambaPlatformAdapter.DecodeStatus(result) ?? new(FileServiceProtocol.Smb, FileServiceRuntimeState.Unavailable, null, false, false, FileServiceProblemCodes.WindowsApiUnavailable)
            : new(FileServiceProtocol.Smb, FileServiceRuntimeState.Unavailable, null, false, false, result.ProblemCode == PrivilegedProblemCode.HelperUnavailable ? FileServiceProblemCodes.HelperUnavailable : FileServiceProblemCodes.WindowsApiUnavailable);
    }
    public async Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct)
    {
        var result = await helper.InstallAsync(id, ct);
        return new(id, result.Success, result.Success ? null : result.ProblemCode switch
        {
            PrivilegedProblemCode.UnsupportedOperation => FileServiceProblemCodes.WindowsServerRequired,
            PrivilegedProblemCode.HelperUnavailable => FileServiceProblemCodes.HelperUnavailable,
            PrivilegedProblemCode.RestartRequired => FileServiceProblemCodes.RestartRequired,
            _ => FileServiceProblemCodes.InstallationFailed,
        });
    }
    public async Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct) => LinuxSambaPlatformAdapter.Result(id,
        await helper.ServiceAsync(action switch { SmbLifecycleAction.Start => SmbServiceAction.Start, SmbLifecycleAction.Stop => SmbServiceAction.Stop, _ => SmbServiceAction.Restart }, id, ct));
    public async Task<IReadOnlyList<FileShareDto>> ReadManagedSharesAsync(CancellationToken ct)
    {
        var result = await helper.ReadManagedConfigurationAsync(Guid.NewGuid(), ct);
        return result.Success ? LinuxSambaPlatformAdapter.Decode<List<FileShareDto>>(result) ?? [] : [];
    }
    public async Task<FileServiceOperationResultDto> ApplyShareAsync(FileShareDto share, string? expectedSnapshot, Guid id, CancellationToken ct) => LinuxSambaPlatformAdapter.Result(id,
        await helper.ApplyWindowsShareAsync(new(share.Id, share.Name, share.Path, share.Description, share.ReadOnly, share.Enabled, share.GuestAllowed,
            share.Permissions.Select(p => new SmbSharePermissionRequest(p.Principal, p.Access.ToString())).ToArray()), expectedSnapshot, id, ct));
    public async Task<FileServiceOperationResultDto> RemoveShareAsync(string id, string? expectedSnapshot, Guid operationId, CancellationToken ct) => LinuxSambaPlatformAdapter.Result(operationId,
        await helper.RemoveWindowsShareAsync(id, expectedSnapshot, operationId, ct));
    public async Task<WindowsSmbSecurityOperationResult> ApplyServerSecurityAsync(string? expectedSnapshot, Guid operationId, CancellationToken ct)
    {
        // Baseline verification and the following mutation belong to one public operation,
        // but the Helper replay guard requires a distinct ID for each wire request.
        var result = await helper.SetWindowsServerSecurityAsync(expectedSnapshot, Guid.NewGuid(), ct);
        if (!result.Success)
        {
            var code = result.ProblemCode switch
            {
                PrivilegedProblemCode.Conflict => FileServiceProblemCodes.ReconciliationRequired,
                PrivilegedProblemCode.HelperUnavailable => FileServiceProblemCodes.HelperUnavailable,
                _ => FileServiceProblemCodes.WindowsApiUnavailable,
            };
            return new(new(operationId, false, code), null);
        }
        var snapshot = LinuxSambaPlatformAdapter.Decode<SmbWindowsServerSecuritySnapshot>(result);
        return snapshot is { Compliant: true } ? new(new(operationId, true), snapshot.SnapshotHash)
            : new(new(operationId, false, FileServiceProblemCodes.WindowsApiUnavailable), null);
    }
}
