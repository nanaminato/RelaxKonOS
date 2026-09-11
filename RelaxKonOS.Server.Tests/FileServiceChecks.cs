using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Server.FileServices;

public static class FileServiceChecks
{
    public static async Task RunAsync()
    {
        var replayTransport = new ReplayCheckingSmbTransport();
        var wireAdapter = new WindowsSmbPlatformAdapter(new PrivilegedSmbOperations(replayTransport));
        var publicOperation = Guid.NewGuid();
        var securityStep = await wireAdapter.ApplyServerSecurityAsync(null, publicOperation, CancellationToken.None);
        var shareStep = await wireAdapter.ApplyShareAsync(new("共享", "共享", @"E:\Test", null, false, true, true, [], true), null, publicOperation, CancellationToken.None);
        Check(securityStep.Operation.Succeeded && shareStep.Succeeded && replayTransport.RequestIds.Count == 2,
            "Security check and share creation use distinct Helper request IDs within one public operation");
        var directory = Directory.CreateTempSubdirectory("smb-validation-");
        try
        {
            var request = new UpsertFileShareRequest("第一个文件夹共享", directory.FullName, "SMB share", false, true, false, []);
            Check(SmbValidators.ValidateShare(request, OperatingSystem.IsWindows()) is null, "Existing directory outside former share root and Chinese name accepted");
            Check(SmbValidators.ValidateShare(request with { GuestAllowed = true }, OperatingSystem.IsWindows()) is null, "Guest read-only access does not force authenticated users read-only");
            Check(SmbValidators.ValidateShare(request with { GuestAllowed = true, ReadOnly = true }, OperatingSystem.IsWindows()) is null, "Read-only guest accepted");
            Check(SmbValidators.ValidateShare(request with { Path = Path.Combine(directory.FullName, "missing") }, OperatingSystem.IsWindows()) is not null, "Missing directory rejected");
            if (OperatingSystem.IsWindows())
                Check(SmbValidators.ValidateShare(request with { Path = Path.GetPathRoot(directory.FullName)! }, true) is null, "Drive root accepted");
        }
        finally { directory.Delete(); }
        var mixed = new RelaxKonOS.Protocol.Privileged.SmbManagedShareRequest("test", "中文共享", "/tmp", null, false, true, true,
            [new("alice", "ReadWrite"), new("bob", "Read")]);
        var config = RelaxKonOS.PrivilegedHelper.SambaShareConfiguration.Serialize([mixed, mixed with { Id = "second", Name = "另一个共享" }]);
        Check(config.Contains("read only = yes") && config.Contains("write list = alice") && config.Contains("guest ok = yes"), "Samba guest defaults read-only while alice can write");
        var parsed = RelaxKonOS.PrivilegedHelper.SambaShareConfiguration.Parse(config.Split('\n'), _ => true);
        Check(parsed.Count == 2 && !parsed[0].ReadOnly && parsed[0].GuestAllowed && parsed[0].Permissions.Any(p => p.Principal == "alice" && p.Access == FileShareAccess.ReadWrite), "Multiple Samba shares round-trip guest mode and authenticated write permissions");
        var allReadOnly = RelaxKonOS.PrivilegedHelper.SambaShareConfiguration.Serialize([mixed with { ReadOnly = true }]);
        Check(allReadOnly.Contains("write list = \n") && !allReadOnly.Contains("write list = alice"), "Samba global read-only has no write-list override");
        if (OperatingSystem.IsWindows()) CheckWindowsGuestAcl();
        Check(RelaxKonOS.PrivilegedHelper.WindowsFeatureInstallationState.Evaluate(0, false) is null,
            "Windows installation in progress must keep polling");
        Check(RelaxKonOS.PrivilegedHelper.WindowsFeatureInstallationState.Evaluate(1, false) is { Success: true },
            "Windows completed installation succeeds");
        Check(RelaxKonOS.PrivilegedHelper.WindowsFeatureInstallationState.Evaluate(1, true) is { ProblemCode: RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.RestartRequired },
            "Windows completed installation preserves restart requirement");
        foreach (var state in new byte[] { 2, 255 })
            Check(RelaxKonOS.PrivilegedHelper.WindowsFeatureInstallationState.Evaluate(state, false) is { Success: false, ProblemCode: RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.InternalError },
                "Windows failed or unknown installation state never succeeds");
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
        // Share fingerprint lifecycle: the ledger must hold the digest, and that same digest is what
        // drift comparison and the Helper's expected-snapshot check consume.
        var sharePlatform = new FakeWindowsPlatform(); var shareLedger = new FakeWindowsLedger();
        var shareProvider = new WindowsSmbFileServiceProvider(sharePlatform, shareLedger);
        var shareDirectory = Directory.CreateTempSubdirectory("smb-ledger-");
        try
        {
            var createRequest = new UpsertFileShareRequest("台账共享", shareDirectory.FullName, null, false, true, false, [new("S-1-5-32-544", FileShareAccess.Read)]);
            var created = await shareProvider.CreateShareAsync(createRequest, Guid.NewGuid(), CancellationToken.None);
            var fingerprint = await shareLedger.GetAsync(createRequest.Name, CancellationToken.None);
            Check(created.Succeeded && fingerprint is not null && fingerprint.SnapshotHash.Length == 64 && !fingerprint.SnapshotHash.Contains('\n'),
                "Ownership ledger records the SHA-256 share fingerprint instead of the raw snapshot text");
            var listed = await shareProvider.ListSharesAsync(CancellationToken.None);
            Check(listed.Count == 1 && listed[0] is { Managed: true, Drifted: false },
                "A freshly created Windows share is owned and never reported as drifted");
            var updated = await shareProvider.UpdateShareAsync(createRequest.Name, createRequest with { GuestAllowed = true }, Guid.NewGuid(), CancellationToken.None);
            Check(updated.Succeeded, "An untouched Windows share updates without a false reconciliation refusal");
            sharePlatform.Shares[0] = sharePlatform.Shares[0] with { ReadOnly = true };
            var drifted = (await shareProvider.ListSharesAsync(CancellationToken.None))[0];
            var refused = await shareProvider.UpdateShareAsync(createRequest.Name, createRequest, Guid.NewGuid(), CancellationToken.None);
            Check(drifted.Drifted && !refused.Succeeded && refused.ProblemCode == FileServiceProblemCodes.ReconciliationRequired,
                "An externally modified Windows share is still detected as drifted and refuses overwrite");
        }
        finally { shareDirectory.Delete(); }
        // Host-side share failures must not be reported as an invalid configuration: the Helper has
        // already validated the payload before it touches the Windows API.
        Check(LinuxSambaPlatformAdapter.Problem(new(false, 1, Error: "Windows SMB share health check failed", ProblemCode: RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.InternalError))
                == FileServiceProblemCodes.WindowsApiUnavailable,
            "A failed Windows share mutation reports an unavailable API instead of an invalid configuration");
        Check(LinuxSambaPlatformAdapter.Problem(new(false, 1, Error: "Windows SMB share changed externally", ProblemCode: RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.Conflict))
                == FileServiceProblemCodes.ReconciliationRequired,
            "An externally changed Windows share still reports configuration drift");
        Check(LinuxSambaPlatformAdapter.Problem(new(false, 1, Error: "invalid Windows SMB share request", ProblemCode: RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.InvalidRequest))
                == FileServiceProblemCodes.ConfigurationInvalid,
            "A rejected Windows share payload still reports an invalid configuration");
        Check(LinuxSambaPlatformAdapter.Problem(new(false, 1, Error: "Samba configuration invalid", ProblemCode: RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.InternalError))
                == FileServiceProblemCodes.ConfigurationInvalid,
            "Non-Windows Helper failures keep their existing classification");
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckWindowsGuestAcl()
    {
        var bytes = RelaxKonOS.PrivilegedHelper.WindowsSmbShareSecurity.CreateDescriptor(
            [new("S-1-5-32-544", "ReadWrite"), new("S-1-1-0", "ReadWrite")], false, true);
        var acl = new System.Security.AccessControl.RawSecurityDescriptor(bytes, 0).DiscretionaryAcl!;
        var entries = acl.Cast<System.Security.AccessControl.CommonAce>().ToArray();
        Check(entries.Any(x => x.SecurityIdentifier.Value == "S-1-5-32-544" && x.AceQualifier == System.Security.AccessControl.AceQualifier.AccessAllowed && (x.AccessMask & 2) != 0), "Windows administrators retain write access with guest enabled");
        foreach (var sid in new[] { "S-1-5-7", "S-1-5-32-546" })
        {
            var deny = entries.Single(x => x.SecurityIdentifier.Value == sid && x.AceQualifier == System.Security.AccessControl.AceQualifier.AccessDenied);
            Check((deny.AccessMask & 0x000D0156) == 0x000D0156 && (deny.AccessMask & 0x00120089) == 0, "Windows guest denies writes without denying reads even with Everyone write");
        }
        var readOnly = new System.Security.AccessControl.RawSecurityDescriptor(RelaxKonOS.PrivilegedHelper.WindowsSmbShareSecurity.CreateDescriptor([new("S-1-5-32-544", "ReadWrite")], true, false), 0);
        Check(readOnly.DiscretionaryAcl!.Cast<System.Security.AccessControl.CommonAce>().All(x => (x.AccessMask & 2) == 0), "Global read-only also applies to administrators");
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
        public List<FileShareDto> Shares { get; } = [];
        public Task<FileServiceStatusDto> DetectAsync(CancellationToken ct) => Task.FromResult(new FileServiceStatusDto(FileServiceProtocol.Smb, FileServiceRuntimeState.Running, "fake", true, true));
        public Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<IReadOnlyList<FileShareDto>> ReadManagedSharesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FileShareDto>>(Shares.ToArray());
        public Task<FileServiceOperationResultDto> ApplyShareAsync(FileShareDto share, string? snapshot, Guid id, CancellationToken ct)
        {
            Shares.RemoveAll(item => item.Id == share.Id); Shares.Add(share with { Managed = true });
            return Task.FromResult(new FileServiceOperationResultDto(id, true));
        }
        public Task<FileServiceOperationResultDto> RemoveShareAsync(string id, string? snapshot, Guid operationId, CancellationToken ct)
        {
            Shares.RemoveAll(item => item.Id == id);
            return Task.FromResult(new FileServiceOperationResultDto(operationId, true));
        }
        public Task<WindowsSmbSecurityOperationResult> ApplyServerSecurityAsync(string? expectedSnapshot, Guid operationId, CancellationToken ct)
        {
            SecurityCalls++;
            return Task.FromResult(FailSecurityWithDrift
                ? new WindowsSmbSecurityOperationResult(new(operationId, false, FileServiceProblemCodes.ReconciliationRequired), null)
                : new WindowsSmbSecurityOperationResult(new(operationId, true), "security-snapshot"));
        }
    }
    private sealed class ReplayCheckingSmbTransport : RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport
    {
        public HashSet<Guid> RequestIds { get; } = [];
        public Task<RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult> ExecuteAsync(RelaxKonOS.Protocol.Privileged.PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (!RequestIds.Add(request.OperationId!.Value))
                return Task.FromResult(new RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult(false, ProblemCode: RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.Conflict));
            var snapshot = new RelaxKonOS.Protocol.Privileged.SmbWindowsServerSecuritySnapshot("security", false, true, true, true, true);
            return Task.FromResult(new RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult(true,
                OutputBase64: Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot))));
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
