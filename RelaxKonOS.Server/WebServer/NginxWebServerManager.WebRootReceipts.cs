using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.WebServer;

internal sealed partial class NginxWebServerManager
{
    private const string AcmeEnabledMarkerContent = OwnershipMarker + "\nACME HTTP-01 enabled.\n";

    internal static async Task<bool> CleanWebRootMarkerStageAsync(IPrivilegedNginxOperations native, WebServerDto instance, CancellationToken ct)
    {
        await IntegrationGate.WaitAsync(ct);
        try
        {
            var directory = GetSitesDirectory(instance);
            if (directory != "/etc/nginx/conf.d/relaxkonos.d" || IsSymbolicLink(directory)) return false;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AcmeEnabledMarkerContent)));
            return await DeleteMatchingWebRootFileAsync(native, Path.Combine(directory, AcmeEnabledFileName + ".stage"), hash, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        finally { IntegrationGate.Release(); }
    }

    private async Task<string?> ApplyTrackedWebRootSiteAsync(WebServerDto instance, WebServerSiteDto site,
        WebServerOperationStore.OperationProgress progress, CancellationToken ct)
    {
        var directory = GetSitesDirectory(instance)!;
        var config = Path.Combine(directory, site.Id + ".conf");
        var backup = config + ".rollback";
        if (!IsRelaxKonOSSiteConfig(config) || !IsAbsent(backup)) return "webserver.site_save_failed";
        var certificate = await ResolveSiteCertificatePathsAsync(site, ct);
        if (site.HttpsEnabled && certificate is null) return "webserver.site_certificate_required";
        if (site.RootPath is not null && (!Directory.Exists(site.RootPath) || IsSymbolicLink(site.RootPath)))
            return "webserver.site_root_invalid";
        var candidate = RenderSiteConfiguration(site, certificate, AcmeChallengeRoot);
        var previousHash = await ReadWebRootHashAsync(config, ct);
        if (previousHash is null) return "webserver.site_save_failed";
        var step = new NginxWebRootStep(site.Id, Guid.NewGuid(), previousHash,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate))));
        var stage = WebRootStage(directory, step.ReloadId);
        if (!IsAbsent(stage)) return "webserver.ownership_conflict";
        await progress.BeginWebRootStepAsync(step, ct);
        try
        {
            var problem = await WriteNginxFileAsync(stage, candidate, ct)
                ? await TestConfigurationAsync(instance, ct) : "webserver.site_save_failed";
            if (problem is not null)
            {
                if (!await CleanWebRootStepUnderGateAsync(privilegedNginx, instance, step, CancellationToken.None))
                    return WebServerOperationStore.NativeRecoveryRequired;
                await progress.EndWebRootStepAsync(step, applied: false, problem);
                return problem;
            }

            var dispatched = step with { Dispatched = true };
            await progress.AdvanceWebRootStepAsync(step, dispatched, ct);
            step = dispatched;
            var completion = CancellationToken.None;
            if (await ReadWebRootHashAsync(config, completion) != step.PreviousHash
                || !await MoveNginxFileAsync(config, backup, overwrite: false, completion)
                || !await MoveNginxFileAsync(stage, config, overwrite: false, completion)
                || await TestConfigurationAsync(instance, completion) is not null)
                return WebServerOperationStore.NativeRecoveryRequired;

            var native = await RunTrackedReloadAsync(step.ReloadId, completion);
            if (native.NginxReloadReceipt is not { } receipt || receipt.OperationId != step.ReloadId || native.Success != receipt.Succeeded)
                return WebServerOperationStore.NativeRecoveryRequired;
            if (!receipt.Succeeded)
            {
                var compensation = step with { CompensationId = Guid.NewGuid() };
                var previous = await ReadWebRootRestoreBytesAsync(directory, compensation, completion);
                if (previous is null)
                    return WebServerOperationStore.NativeRecoveryRequired;
                await progress.AdvanceWebRootStepAsync(step, compensation, completion);
                step = compensation;
                if (!await RestoreTrackedWebRootSiteAsync(privilegedNginx, directory, step, previous))
                    return WebServerOperationStore.NativeRecoveryRequired;
            }
            if (!await CleanWebRootStepUnderGateAsync(privilegedNginx, instance, step, completion))
                return WebServerOperationStore.NativeRecoveryRequired;
            await progress.EndWebRootStepAsync(step, applied: step.CompensationId is null);
            return step.CompensationId is null ? null : "webserver.site_reload_failed";
        }
        catch (Exception error) when (error is not (ResidentWorkCompletionException or NginxProcessCleanupException))
        {
            // The existing owner must retain the checkpoint even if cancellation arrived during staging.
            return WebServerOperationStore.NativeRecoveryRequired;
        }
    }

    internal static Task<bool> VerifyNamedWebRootCompensationAsync(IPrivilegedNginxOperations native, WebServerDto instance,
        NginxWebRootStep step, CancellationToken ct) => WithNamedWebRootEvidenceAsync(native, instance, step, dispatch: false, ct);

    internal static Task<bool> ExecuteNamedWebRootCompensationAsync(IPrivilegedNginxOperations native, WebServerDto instance,
        NginxWebRootStep step, CancellationToken ct) => WithNamedWebRootEvidenceAsync(native, instance, step, dispatch: true, ct);

    private static async Task<bool> WithNamedWebRootEvidenceAsync(IPrivilegedNginxOperations native, WebServerDto instance,
        NginxWebRootStep step, bool dispatch, CancellationToken ct)
    {
        await IntegrationGate.WaitAsync(ct);
        try
        {
            if (GetTrackedWebRootDirectory(instance, step) is not { } directory
                || !IsAbsent(Path.Combine(directory, AcmeEnabledFileName + ".stage")))
                return false;
            var result = await native.QueryReloadReceiptAsync(step.ReloadId, ct);
            if (result is not { Success: true, NginxReloadReceipt: { Succeeded: false } receipt }
                || receipt.OperationId != step.ReloadId || !(await native.ConfirmServiceIdleAsync(ct)).Success)
                return false;
            var previous = await ReadWebRootRestoreBytesAsync(directory, step, ct);
            if (previous is null) return false;
            ct.ThrowIfCancellationRequested();
            return !dispatch || await RestoreTrackedWebRootSiteAsync(native, directory, step, previous);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        finally { IntegrationGate.Release(); }
    }

    private static async Task<byte[]?> ReadWebRootRestoreBytesAsync(string directory, NginxWebRootStep step, CancellationToken ct)
    {
        var config = Path.Combine(directory, step.SiteId + ".conf");
        var backup = config + ".rollback";
        if (!IsAbsent(WebRootStage(directory, step.ReloadId))
            || step.CompensationId is { } compensation && !IsAbsent(WebRootStage(directory, compensation))
            || await ReadWebRootHashAsync(config, ct) != step.CandidateHash
            || await ReadWebRootHashAsync(backup, ct) != step.PreviousHash)
            return null;
        var previous = await File.ReadAllBytesAsync(backup, ct);
        return Convert.ToHexString(SHA256.HashData(previous)) == step.PreviousHash ? previous : null;
    }

    private static async Task<bool> RestoreTrackedWebRootSiteAsync(IPrivilegedNginxOperations native, string directory,
        NginxWebRootStep step, byte[] previous)
    {
        if (step.CompensationId is not { } compensation) return false;
        var stage = WebRootStage(directory, compensation);
        var config = Path.Combine(directory, step.SiteId + ".conf");
        var completion = CancellationToken.None;
        if (!(await native.WriteManagedFileAsync(stage, previous, completion)).Success
            || await ReadWebRootHashAsync(config, completion) != step.CandidateHash
            || !(await native.MoveManagedFileAsync(stage, config, overwrite: true, completion)).Success
            || !(await native.TestConfigurationAsync(completion)).Success)
            return false;
        var restored = await native.RunTrackedReloadAsync(compensation, completion);
        return restored is { Success: true, NginxReloadReceipt: { Succeeded: true } receipt } && receipt.OperationId == compensation;
    }

    internal static async Task<bool> ReconcileWebRootStepAsync(IPrivilegedNginxOperations native, WebServerDto instance,
        NginxWebRootStep step, CancellationToken ct)
    {
        await IntegrationGate.WaitAsync(ct);
        try
        {
            if (step.Dispatched)
            {
                var id = step.CompensationId ?? step.ReloadId;
                var evidence = await native.QueryReloadReceiptAsync(id, ct);
                if (evidence is not { Success: true, NginxReloadReceipt: { Succeeded: true } receipt } || receipt.OperationId != id)
                    return false;
            }
            return await CleanWebRootStepUnderGateAsync(native, instance, step, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        finally { IntegrationGate.Release(); }
    }

    private static async Task<bool> CleanWebRootStepUnderGateAsync(IPrivilegedNginxOperations native, WebServerDto instance,
        NginxWebRootStep step, CancellationToken ct)
    {
        if (GetTrackedWebRootDirectory(instance, step) is not { } directory) return false;
        var config = Path.Combine(directory, step.SiteId + ".conf");
        var backup = config + ".rollback";
        var stage = WebRootStage(directory, step.ReloadId);
        var expected = step.Dispatched && step.CompensationId is null ? step.CandidateHash : step.PreviousHash;
        if (await ReadWebRootHashAsync(config, ct) != expected) return false;
        if (!step.Dispatched)
            return IsAbsent(backup) && await DeleteMatchingWebRootFileAsync(native, stage, step.CandidateHash, ct);
        if (!IsAbsent(stage) || step.CompensationId is { } compensation && !IsAbsent(WebRootStage(directory, compensation)))
            return false;
        return await DeleteMatchingWebRootFileAsync(native, backup, step.PreviousHash, ct);
    }

    private static string? GetTrackedWebRootDirectory(WebServerDto instance, NginxWebRootStep step) =>
        OperatingSystem.IsLinux() && instance.ExecutablePath == "/usr/sbin/nginx" && instance.ConfigurationPath == "/etc/nginx/nginx.conf"
            && instance.Id == InstanceId(instance.ExecutablePath) && SiteIdPattern().IsMatch(step.SiteId)
            && GetSitesDirectory(instance) is "/etc/nginx/conf.d/relaxkonos.d" and var directory
            && !IsSymbolicLink(directory) && IsAcmeHttp01Enabled(directory) ? directory : null;

    private static string WebRootStage(string directory, Guid id) => Path.Combine(directory, $"relaxkonos.{id:N}.conf");

    private static async Task<bool> DeleteMatchingWebRootFileAsync(IPrivilegedNginxOperations native, string path, string expectedHash, CancellationToken ct)
    {
        var hash = await ReadWebRootHashAsync(path, ct);
        if (hash is null) return true;
        if (hash != expectedHash) return false;
        var result = await native.DeleteManagedFileAsync(path, ct);
        return result.Success || result.ProblemCode == PrivilegedProblemCode.NotFound;
    }

    private static async Task<string?> ReadWebRootHashAsync(string path, CancellationToken ct)
    {
        if (IsAbsent(path)) return null;
        if (IsSymbolicLink(path) || !File.Exists(path)) throw new IOException("The WebRoot configuration path is unsafe.");
        await using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
    }
}
