using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Server.FileServices;

public static class FileServiceChecks
{
    public static async Task RunAsync()
    {
        var provider = new FakeProvider();
        var manager = new FileServiceManager(new FileServiceProviderResolver([provider]));
        var status = await manager.GetStatusAsync(CancellationToken.None);
        Check(status.Protocol == FileServiceProtocol.Smb && status.State == FileServiceRuntimeState.Running, "SMB resolver selects the applicable provider");
        var connection = manager.GetConnectionInfo();
        Check(connection.Port == 445 && connection.WindowsUncPrefix.StartsWith("\\\\", StringComparison.Ordinal) && connection.SmbUriPrefix.StartsWith("smb://", StringComparison.Ordinal), "Connection information exposes only SMB host/port prefixes");
        var unsupported = new FileServiceManager(new FileServiceProviderResolver([]));
        var unsupportedStatus = await unsupported.GetStatusAsync(CancellationToken.None);
        Check(unsupportedStatus.State == FileServiceRuntimeState.Unsupported && unsupportedStatus.HealthProblemCode == FileServiceProblemCodes.UnsupportedPlatform, "Missing provider fails closed as unsupported platform");
        var result = await manager.LifecycleAsync(SmbLifecycleAction.Restart, CancellationToken.None);
        Check(result.Succeeded && provider.LifecycleCalls == 1, "Manager dispatches lifecycle through provider abstraction");
        await Task.WhenAll(
            manager.LifecycleAsync(SmbLifecycleAction.Start, CancellationToken.None),
            manager.LifecycleAsync(SmbLifecycleAction.Restart, CancellationToken.None));
        Check(provider.MaximumConcurrentLifecycleCalls == 1, "Manager serializes concurrent SMB mutations");
        var windows = new FakeWindowsPlatform(); var windowsLedger = new FakeWindowsLedger();
        var windowsProvider = new WindowsSmbFileServiceProvider(windows, windowsLedger);
        var lifecycle = await windowsProvider.LifecycleAsync(SmbLifecycleAction.Start, Guid.NewGuid(), CancellationToken.None);
        Check(lifecycle.Succeeded && windows.SecurityCalls == 1 && (await windowsLedger.GetServerSecurityAsync(CancellationToken.None))?.SnapshotHash == "security-snapshot",
            "Windows lifecycle establishes the fixed server-security snapshot before service mutation");
        windows.FailSecurityWithDrift = true;
        var drift = await windowsProvider.LifecycleAsync(SmbLifecycleAction.Restart, Guid.NewGuid(), CancellationToken.None);
        Check(!drift.Succeeded && drift.ProblemCode == FileServiceProblemCodes.ReconciliationRequired && (await windowsLedger.GetServerSecurityAsync(CancellationToken.None))?.ReconciliationRequired == true,
            "Windows server-security drift is fail-closed and marked for reconciliation");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS FILE SERVICES: " + message);
    }
    private sealed class FakeProvider : IFileServiceProvider
    {
        public int LifecycleCalls { get; private set; }
        public int MaximumConcurrentLifecycleCalls { get; private set; }
        private int _activeLifecycleCalls;
        public FileServiceProtocol Protocol => FileServiceProtocol.Smb;
        public bool IsApplicable => true;
        public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct) => Task.FromResult(new FileServiceStatusDto(FileServiceProtocol.Smb, FileServiceRuntimeState.Running, "fake", true, true));
        public Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new FileServiceCapabilitiesDto(true, false, false, true, false));
        public Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public async Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct)
        {
            LifecycleCalls++;
            var active = Interlocked.Increment(ref _activeLifecycleCalls);
            MaximumConcurrentLifecycleCalls = Math.Max(MaximumConcurrentLifecycleCalls, active);
            try { await Task.Delay(10, ct); return new(id, true); }
            finally { Interlocked.Decrement(ref _activeLifecycleCalls); }
        }
        public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FileShareDto>>([]);
        public Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, Guid operationId, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(operationId, true));
        public Task<FileServiceOperationResultDto> DeleteShareAsync(string id, Guid operationId, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(operationId, true));
        public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FileServiceUserDto>>([]);
        public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<FileServiceOperationResultDto> SetUserPasswordAsync(string username, string password, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
    }
    private sealed class FakeWindowsPlatform : IWindowsSmbPlatformAdapter
    {
        public int SecurityCalls { get; private set; }
        public bool FailSecurityWithDrift { get; set; }
        public Task<FileServiceStatusDto> DetectAsync(CancellationToken ct) => Task.FromResult(new FileServiceStatusDto(FileServiceProtocol.Smb, FileServiceRuntimeState.Running, "fake", true, true));
        public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<IReadOnlyList<FileShareDto>> ReadManagedSharesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FileShareDto>>([]);
        public Task<FileServiceOperationResultDto> ApplyShareAsync(FileShareDto share, string? snapshot, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<FileServiceOperationResultDto> RemoveShareAsync(string id, string? snapshot, Guid operationId, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(operationId, true));
        public Task<WindowsSmbSecurityOperationResult> ApplyServerSecurityAsync(string? expectedSnapshot, Guid operationId, CancellationToken ct)
        {
            SecurityCalls++;
            return Task.FromResult(FailSecurityWithDrift
                ? new WindowsSmbSecurityOperationResult(new(operationId, false, FileServiceProblemCodes.ReconciliationRequired), null)
                : new WindowsSmbSecurityOperationResult(new(operationId, true), "security-snapshot"));
        }
    }
    private sealed class FakeWindowsLedger : IWindowsSmbOwnershipLedger
    {
        private readonly Dictionary<string, WindowsSmbOwnershipRecord> _shares = new(StringComparer.Ordinal);
        private WindowsSmbServerSecurityRecord? _security;
        public Task<WindowsSmbOwnershipRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(_shares.GetValueOrDefault(id));
        public Task<IReadOnlyDictionary<string, WindowsSmbOwnershipRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, WindowsSmbOwnershipRecord>>(_shares);
        public Task UpsertAsync(WindowsSmbOwnershipRecord record, CancellationToken ct) { _shares[record.Id] = record; return Task.CompletedTask; }
        public Task RemoveAsync(string id, CancellationToken ct) { _shares.Remove(id); return Task.CompletedTask; }
        public Task<WindowsSmbServerSecurityRecord?> GetServerSecurityAsync(CancellationToken ct) => Task.FromResult(_security);
        public Task UpsertServerSecurityAsync(WindowsSmbServerSecurityRecord record, CancellationToken ct) { _security = record; return Task.CompletedTask; }
    }
}
