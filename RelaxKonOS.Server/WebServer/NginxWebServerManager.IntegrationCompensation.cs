using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Server.WebServer;

internal sealed partial class NginxWebServerManager
{
    internal static Task<bool> VerifyIntegrationCompensationAsync(IPrivilegedNginxOperations native, WebServerMetadataRepository metadata,
        Guid originalId, NginxIntegrationCheckpoint checkpoint, CancellationToken ct) =>
        WithIntegrationCompensationEvidenceAsync(native, metadata, originalId, checkpoint, _ => Task.FromResult(true), ct);

    internal static async Task ExecuteIntegrationCompensationAsync(IPrivilegedNginxOperations native, WebServerMetadataRepository metadata,
        Guid originalId, Guid compensationId, NginxIntegrationCheckpoint checkpoint, CancellationToken ct)
    {
        await WithIntegrationCompensationEvidenceAsync(native, metadata, originalId, checkpoint, async anchor =>
        {
            ct.ThrowIfCancellationRequested();
            var completion = CancellationToken.None;
            if (!(await native.DeleteManagedFileAsync(anchor, completion)).Success) return false;
            if (!(await native.TestConfigurationAsync(completion)).Success) return false;
            // A missing reply leaves the durable compensation ID for read-only reconciliation.
            _ = await native.RunTrackedReloadAsync(compensationId, completion);
            return true;
        }, ct);
    }

    internal static async Task<bool> ConfirmIntegrationCompensationAsync(WebServerMetadataRepository metadata,
        Guid originalId, NginxIntegrationCheckpoint checkpoint, CancellationToken ct)
    {
        await IntegrationGate.WaitAsync(ct);
        try
        {
            var anchor = await ReadCompensationAnchorAsync(metadata, originalId, checkpoint, ct);
            return anchor is not null && IsAbsent(anchor);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        finally { IntegrationGate.Release(); }
    }

    private static async Task<bool> WithIntegrationCompensationEvidenceAsync(IPrivilegedNginxOperations native,
        WebServerMetadataRepository metadata, Guid originalId, NginxIntegrationCheckpoint checkpoint,
        Func<string, Task<bool>> execute, CancellationToken ct)
    {
        await IntegrationGate.WaitAsync(ct);
        try
        {
            var result = await native.QueryReloadReceiptAsync(originalId, ct);
            if (result is not { Success: true, NginxReloadReceipt: { Succeeded: false } receipt } || receipt.OperationId != originalId)
                return false;
            var anchor = await ReadCompensationAnchorAsync(metadata, originalId, checkpoint, ct);
            if (anchor is null || IsSymbolicLink(anchor) || !File.Exists(anchor)
                || !await MatchesSiteConfigAsync(anchor, AnchorContent(Path.Combine(LinuxSystemIncludeDirectory, "relaxkonos.d")), ct)
                || !(await native.ConfirmServiceIdleAsync(ct)).Success)
                return false;
            return await execute(anchor);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        finally { IntegrationGate.Release(); }
    }

    private static async Task<string?> ReadCompensationAnchorAsync(WebServerMetadataRepository metadata, Guid originalId,
        NginxIntegrationCheckpoint checkpoint, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || checkpoint.Instance is not { ConfigurationPath: { } configuration } instance
            || instance.ManagementMode != WebServerManagementMode.Integrated || instance.ProviderId != ProviderKey
            || instance.Id != InstanceId(instance.ExecutablePath)
            || FindOwnedIncludeDirectory(configuration) != LinuxSystemIncludeDirectory || IsSymbolicLink(LinuxSystemIncludeDirectory)
            || !await metadata.IsSnapshotCurrentAsync(configuration, checkpoint.Snapshot, ct))
            return null;

        var stage = Path.Combine(LinuxSystemIncludeDirectory, $"relaxkonos.{originalId:N}.conf");
        var sites = Path.Combine(LinuxSystemIncludeDirectory, "relaxkonos.d");
        if (!IsAbsent(stage) || !IsAbsent(sites) && (IsSymbolicLink(sites) || !Directory.Exists(sites)
            || Directory.EnumerateFileSystemEntries(sites).Any()))
            return null;
        return Path.Combine(LinuxSystemIncludeDirectory, OwnedFileName);
    }

    private static bool IsAbsent(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
}
