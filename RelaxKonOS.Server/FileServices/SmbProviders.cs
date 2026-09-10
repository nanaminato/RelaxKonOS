using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Server.FileServices;

public sealed class LinuxSambaFileServiceProvider(ISambaPlatformAdapter platform) : IFileServiceProvider
{
    public FileServiceProtocol Protocol => FileServiceProtocol.Smb;
    public bool IsApplicable => OperatingSystem.IsLinux();
    public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct) => platform.DetectAsync(ct);
    public async Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        return new(status.State != FileServiceRuntimeState.Unsupported, true, true, true, false, status.HealthProblemCode);
    }
    public Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct) => platform.InstallAsync(id, ct);
    public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct) => platform.LifecycleAsync(action, id, ct);
    public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct) => platform.ReadManagedSharesAsync(ct);
    public async Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, Guid id, CancellationToken ct)
    {
        if (SmbValidators.ValidateShare(request, windows: false) is { } invalid) return new(id, false, invalid);
        var shares = (await ListSharesAsync(ct)).ToList();
        if (shares.Any(s => string.Equals(s.Name, request.Name, StringComparison.OrdinalIgnoreCase))) return new(id, false, FileServiceProblemCodes.ShareConflict);
        shares.Add(ToDto(id.ToString("N"), request));
        return await platform.ApplySharesAsync(shares, ct);
    }
    public async Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, Guid operationId, CancellationToken ct)
    {
        if (SmbValidators.ValidateShare(request, windows: false) is { } invalid) return new(operationId, false, invalid);
        var shares = (await ListSharesAsync(ct)).ToList();
        var index = shares.FindIndex(s => s.Id == id && s.Managed);
        if (index < 0) return new(operationId, false, FileServiceProblemCodes.ConfigurationUnmanaged);
        shares[index] = ToDto(id, request);
        return await platform.ApplySharesAsync(shares, ct);
    }
    public async Task<FileServiceOperationResultDto> DeleteShareAsync(string id, Guid operationId, CancellationToken ct)
    {
        var shares = (await ListSharesAsync(ct)).ToList();
        var removed = shares.RemoveAll(s => s.Id == id && s.Managed);
        return removed == 0 ? new(operationId, false, FileServiceProblemCodes.ConfigurationUnmanaged) : await platform.ApplySharesAsync(shares, ct);
    }
    public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct) => platform.ReadUsersAsync(ct);
    public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, Guid id, CancellationToken ct) => !SmbValidators.IsValidUsername(username)
        ? Task.FromResult(new FileServiceOperationResultDto(id, false, FileServiceProblemCodes.SystemAccountNotFound)) : platform.SetUserAsync(username, enabled, null, id, ct);
    public Task<FileServiceOperationResultDto> SetUserPasswordAsync(string username, string password, Guid id, CancellationToken ct) => !SmbValidators.IsValidUsername(username) || !SmbValidators.IsValidPassword(password)
        ? Task.FromResult(new FileServiceOperationResultDto(id, false, FileServiceProblemCodes.SystemAccountNotFound)) : platform.SetUserAsync(username, true, password, id, ct);
    private static FileShareDto ToDto(string id, UpsertFileShareRequest request) => new(id, request.Name, Path.GetFullPath(request.Path), request.Description, request.ReadOnly,
        request.Enabled, request.GuestAllowed, request.Permissions, true);
}

public sealed class WindowsSmbFileServiceProvider(IWindowsSmbPlatformAdapter platform, IWindowsSmbOwnershipLedger ledger) : IFileServiceProvider
{
    public FileServiceProtocol Protocol => FileServiceProtocol.Smb;
    public bool IsApplicable => OperatingSystem.IsWindows();
    public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct) => platform.DetectAsync(ct);
    public async Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        return new(status.State != FileServiceRuntimeState.Unsupported, false, false, true, true, status.HealthProblemCode);
    }
    public Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, false, FileServiceProblemCodes.WindowsApiUnavailable));
    public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct) => platform.LifecycleAsync(action, id, ct);
    public async Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct)
    {
        var shares = await platform.ReadManagedSharesAsync(ct);
        var owned = await ledger.ListAsync(ct);
        return shares.Select(share => owned.TryGetValue(share.Id, out var snapshot)
            ? share with { Managed = true, Drifted = !string.Equals(snapshot.PathHash, WindowsSmbOwnershipLedger.PathHash(share.Path), StringComparison.Ordinal)
                || !string.Equals(snapshot.Snapshot, WindowsSmbOwnershipLedger.SnapshotHash(Snapshot(share)), StringComparison.Ordinal) }
            : share with { Managed = false }).ToArray();
    }
    public async Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, Guid id, CancellationToken ct)
    {
        if (SmbValidators.ValidateShare(request, windows: true) is { } invalid) return new(id, false, invalid);
        var actual = await platform.ReadManagedSharesAsync(ct);
        if (actual.Any(s => string.Equals(s.Name, request.Name, StringComparison.OrdinalIgnoreCase))) return new(id, false, FileServiceProblemCodes.ShareConflict);
        // Windows NetShare APIs use the share name as their immutable system resource key.
        // The ledger may never synthesize a second identifier that cannot be re-read from API state.
        var share = ToDto(request.Name, request);
        var applied = await platform.ApplyShareAsync(share, null, id, ct);
        if (applied.Succeeded && (await platform.ReadManagedSharesAsync(ct)).FirstOrDefault(item => item.Id == share.Id) is { } actualShare)
            await ledger.UpsertAsync(new(actualShare.Id, actualShare.Name, WindowsSmbOwnershipLedger.PathHash(actualShare.Path), Snapshot(actualShare), false), ct);
        return applied;
    }
    public async Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, Guid operationId, CancellationToken ct)
    {
        if (SmbValidators.ValidateShare(request, windows: true) is { } invalid) return new(operationId, false, invalid);
        var record = await ledger.GetAsync(id, ct);
        var actual = (await platform.ReadManagedSharesAsync(ct)).FirstOrDefault(s => s.Id == id);
        if (record is null || actual is null || !string.Equals(record.PathHash, WindowsSmbOwnershipLedger.PathHash(actual.Path), StringComparison.Ordinal)
            || !string.Equals(record.Snapshot, WindowsSmbOwnershipLedger.SnapshotHash(Snapshot(actual)), StringComparison.Ordinal))
            return new(operationId, false, FileServiceProblemCodes.ReconciliationRequired);
        var replacement = ToDto(id, request);
        var applied = await platform.ApplyShareAsync(replacement, record.Snapshot, operationId, ct);
        if (applied.Succeeded && (await platform.ReadManagedSharesAsync(ct)).FirstOrDefault(item => item.Id == replacement.Id) is { } actualShare)
            await ledger.UpsertAsync(new(actualShare.Id, actualShare.Name, WindowsSmbOwnershipLedger.PathHash(actualShare.Path), Snapshot(actualShare), false), ct);
        return applied;
    }
    public async Task<FileServiceOperationResultDto> DeleteShareAsync(string id, Guid operationId, CancellationToken ct)
    {
        var record = await ledger.GetAsync(id, ct);
        if (record is null) return new(operationId, false, FileServiceProblemCodes.ConfigurationUnmanaged);
        var actual = (await platform.ReadManagedSharesAsync(ct)).FirstOrDefault(s => s.Id == id);
        if (actual is null || !string.Equals(record.PathHash, WindowsSmbOwnershipLedger.PathHash(actual.Path), StringComparison.Ordinal)
            || !string.Equals(record.Snapshot, WindowsSmbOwnershipLedger.SnapshotHash(Snapshot(actual)), StringComparison.Ordinal)) return new(operationId, false, FileServiceProblemCodes.ReconciliationRequired);
        var deleted = await platform.RemoveShareAsync(id, record.Snapshot, operationId, ct);
        if (deleted.Succeeded) await ledger.RemoveAsync(id, ct);
        return deleted;
    }
    public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FileServiceUserDto>>([]);
    public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, false, FileServiceProblemCodes.WindowsApiUnavailable));
    public Task<FileServiceOperationResultDto> SetUserPasswordAsync(string username, string password, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, false, FileServiceProblemCodes.WindowsApiUnavailable));
    private static FileShareDto ToDto(string id, UpsertFileShareRequest request) => new(id, request.Name, Path.GetFullPath(request.Path), request.Description, request.ReadOnly, request.Enabled, request.GuestAllowed, request.Permissions, true);
    private static string Snapshot(FileShareDto share) => $"{share.Name}\n{share.Path}\n{share.ReadOnly}\n{share.Enabled}\n{share.GuestAllowed}\n{string.Join(',', share.Permissions.Select(p => p.Principal + ':' + p.Access))}";
}
