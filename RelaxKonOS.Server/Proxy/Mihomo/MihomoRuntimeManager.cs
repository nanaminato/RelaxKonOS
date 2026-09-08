using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>
/// Owns only RelaxKonOS-managed Mihomo artifacts. Release directories are immutable; the active
/// pointer changes only after the constrained service boundary and loopback controller both pass.
/// </summary>
public sealed class MihomoRuntimeManager(
    IProxyPlatformPaths paths,
    IHttpClientFactory httpClients,
    IProxyPrivilegedOperations privileged,
    IMihomoRuntimeProbe probe,
    IMihomoControllerClient controller,
    IProxyControllerSecretStore controllerSecrets,
    MihomoControllerOptions controllerOptions,
    MihomoRuntimeManifest manifest,
    IProxyDiagnosticLogStore? diagnostics = null,
    IProxyGeoDataService? geoData = null) : IProxyRuntimeManager, IMihomoConfigurationValidator
{
    private const string ServiceName = "remoteos-mihomo";
    private const string ServiceConfigurationId = "mihomo-default";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProxyRuntimeDto> GetAsync(string engineId, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id) return Unsupported(engineId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            if (state?.ActiveVersion is not { Length: > 0 } active)
                return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false, ProxyProblemCodes.RuntimeNotInstalled);
            if (!File.Exists(ExecutablePath(active)))
                return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, active, state.PreviousVersion, false, false, ProxyProblemCodes.RuntimeNotInstalled);

            // Runtime status is intentionally read-only. Opening Proxy Manager or pressing its
            // ordinary refresh button must never rewrite configuration or restart Mihomo: a
            // restart can make provider-backed subscriptions fetch again. Managed
            // controller/GEO settings are applied by explicit configuration and subscription
            // refresh operations instead.
            var controllerHealth = await controller.IsReachableAsync(cancellationToken);
            // An HTTP 401 proves Mihomo is listening even though its credential is wrong; the
            // health projection reports that mismatch separately. Other failures mean the
            // installed runtime is not currently reachable as a running service.
            var runtimeState = controllerHealth.Succeeded || controllerHealth.ProblemCode == ProxyProblemCodes.ControllerAuthenticationFailed
                ? ProxyRuntimeState.Running
                : ProxyRuntimeState.Stopped;
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, runtimeState, active, state.PreviousVersion, true, false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Validates a candidate profile with the active, verified managed binary. This deliberately
    /// does not depend on the controller or service being running: importing a subscription must
    /// be possible before the profile is activated.
    /// </summary>
    public async Task<string?> ValidateAsync(string configurationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configurationPath) || !Path.IsPathFullyQualified(configurationPath) || !File.Exists(configurationPath))
            return ProxyProblemCodes.ConfigInvalid;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            var executable = state?.ActiveVersion is { Length: > 0 } active ? ExecutablePath(active) : null;
            if (executable is null || !File.Exists(executable)) return ProxyProblemCodes.RuntimeNotInstalled;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            // The executable, arguments, and profile path are all Server-derived; no API input
            // is executed as a command.
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add("-d");
            process.StartInfo.ArgumentList.Add(paths.GetEngineDataDirectory(MihomoEngine.Id));
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(configurationPath);
            try
            {
                if (!process.Start()) return ProxyProblemCodes.RuntimeHealthCheckFailed;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                // Keep draining output after the deadline so a timed-out validation still leaves
                // a safe diagnostic category. Passing the timeout token to ReadToEndAsync used
                // to discard the only useful Mihomo error before it could be classified.
                var standardOut = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* The validation process won the exit race. */ }
                    await Task.WhenAll(standardOut, standardError);
                    var problem = ClassifyConfigurationValidationFailure((await standardOut) + "\n" + (await standardError), timedOut: true);
                    await WriteDiagnosticAsync("warning", ConfigurationValidationDiagnostic(problem, timedOut: true), cancellationToken);
                    return problem;
                }

                await Task.WhenAll(standardOut, standardError);
                if (process.ExitCode == 0) return null;
                var failure = ClassifyConfigurationValidationFailure((await standardOut) + "\n" + (await standardError), timedOut: false);
                await WriteDiagnosticAsync("warning", ConfigurationValidationDiagnostic(failure, timedOut: false), cancellationToken);
                return failure;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                await WriteDiagnosticAsync("warning", "Mihomo configuration validation could not be started: " + exception.GetType().Name, cancellationToken);
                return ProxyProblemCodes.RuntimeHealthCheckFailed;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyRuntimeDto> DetectExternalAsync(string engineId, string executablePath, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id) return Unsupported(engineId);
        if (!TrySafeExternalPath(executablePath, out var path) || !File.Exists(path) || Directory.Exists(path)
            || !HasExpectedArchitecture(path) || (!OperatingSystem.IsWindows() && !IsExecutable(path)))
            return new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Failed, null, null, false, true, ProxyProblemCodes.ExternalRuntimeInvalid);
        var version = await probe.GetVersionAsync(path, cancellationToken);
        return version is null
            ? new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Failed, null, null, false, true, ProxyProblemCodes.ExternalRuntimeInvalid)
            : new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Stopped, version, null, false, true);
    }

    public Task<ProxyRuntimeDto> InstallManagedAsync(string engineId, string? version, CancellationToken cancellationToken) =>
        InstallManagedAsync(engineId, version, null, cancellationToken);

    public Task<ProxyRuntimeDto> InstallManagedAsync(string engineId, string? version, Func<string, Task>? stageReporter, CancellationToken cancellationToken) =>
        engineId != MihomoEngine.Id
            ? Task.FromResult(Unsupported(engineId))
            : InstallManagedCoreAsync(version, async (release, destination, token) =>
            {
                await WriteDiagnosticAsync("info", "Managed Mihomo installation is downloading the trusted release.", token);
                await ReportStageAsync(stageReporter, "downloading");
                await DownloadAndVerifyAsync(release, destination, token);
            }, stageReporter, cancellationToken);

    public Task<ProxyRuntimeDto> InstallManagedFromArchiveAsync(string engineId, string? version, string archivePath, CancellationToken cancellationToken) =>
        InstallManagedFromArchiveAsync(engineId, version, archivePath, null, cancellationToken);

    public Task<ProxyRuntimeDto> InstallManagedFromArchiveAsync(string engineId, string? version, string archivePath, Func<string, Task>? stageReporter, CancellationToken cancellationToken) =>
        engineId != MihomoEngine.Id
            ? Task.FromResult(Unsupported(engineId))
            : InstallManagedCoreAsync(version, async (release, destination, token) =>
            {
                await WriteDiagnosticAsync("info", "Managed Mihomo installation is reading the selected Server archive.", token);
                await ReportStageAsync(stageReporter, "copying");
                await CopyAndVerifyArchiveAsync(release, archivePath, destination, token);
            }, stageReporter, cancellationToken);

    private async Task<ProxyRuntimeDto> InstallManagedCoreAsync(string? version, Func<MihomoRuntimeRelease, string, CancellationToken, Task> stageArchiveAsync, Func<string, Task>? stageReporter, CancellationToken cancellationToken)
    {
        var release = manifest.Find(version);
        if (release is null)
        {
            await WriteDiagnosticAsync("warning", "Managed Mihomo installation was rejected because the requested release is not trusted for this host.", cancellationToken);
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Failed, null, null, false, false,
                MihomoRuntimeManifest.CurrentRid() == "unsupported" ? ProxyProblemCodes.RuntimeUnsupportedPlatform : ProxyProblemCodes.RuntimeVersionUnsupported);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ReportStageAsync(stageReporter, "preparing");
            var before = await ReadStateAsync(cancellationToken);
            await WriteDiagnosticAsync("info", before?.ActiveVersion is { Length: > 0 }
                ? "Managed Mihomo installation is preparing a runtime update."
                : "Managed Mihomo installation is preparing the first runtime activation.", cancellationToken);
            var finalDirectory = VersionDirectory(release.Version);
            try
            {
                var serviceInstalled = false;
                if (!Directory.Exists(finalDirectory))
                    await StageAndInstallReleaseAsync(release, finalDirectory, stageArchiveAsync, stageReporter, cancellationToken);

                await ReportStageAsync(stageReporter, "checking");
                if (await probe.GetVersionAsync(ExecutablePath(release.Version), cancellationToken) is null)
                {
                    await WriteDiagnosticAsync("warning", "Managed Mihomo runtime verification failed after extraction.", cancellationToken);
                    return Failure(before, ProxyProblemCodes.RuntimeHealthCheckFailed);
                }

                if (geoData is not null)
                {
                    var geoProblem = await geoData.EnsureBundledAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(geoProblem))
                    {
                        await WriteDiagnosticAsync("warning", "Managed Mihomo installation could not stage its bundled GEO data: " + geoProblem, cancellationToken);
                        return Failure(before, geoProblem);
                    }
                }

                await ReportStageAsync(stageReporter, "activating");
                var runtimeOperation = before?.ActiveVersion is { Length: > 0 }
                    ? await privileged.ReplaceRuntimeAsync(new ReplaceProxyRuntimeOperation(MihomoEngine.Id, release.Version, ReleaseDirectoryId(release.Version)), cancellationToken)
                    : await privileged.InstallRuntimeAsync(new InstallProxyRuntimeOperation(MihomoEngine.Id, release.Version, ReleaseDirectoryId(release.Version)), cancellationToken);
                if (!runtimeOperation.Succeeded)
                {
                    await WriteDiagnosticAsync("warning", "Managed Mihomo runtime activation failed: " + runtimeOperation.ProblemCode, cancellationToken);
                    return Failure(before, runtimeOperation.ProblemCode);
                }

                // First boot has no Profile yet. This fixed bootstrap file intentionally keeps TUN
                // off and only exposes the controller on loopback; Goal 4 replaces it transactionally.
                var bootstrap = await EnsureBootstrapConfigurationAsync(cancellationToken);
                if (!bootstrap.Succeeded)
                {
                    await WriteDiagnosticAsync("warning", "Managed Mihomo bootstrap configuration failed: " + bootstrap.ProblemCode, cancellationToken);
                    return await RestorePreviousAsync(before, release.Version, bootstrap.ProblemCode, serviceInstalled, cancellationToken);
                }

                if (before?.ActiveVersion is null)
                {
                    await ReportStageAsync(stageReporter, "installing_service");
                    var installService = await privileged.InstallServiceAsync(new InstallProxyServiceOperation(MihomoEngine.Id, ServiceName, ServiceConfigurationId), cancellationToken);
                    if (!installService.Succeeded)
                    {
                        await WriteDiagnosticAsync("warning", "Managed Mihomo service installation failed: " + installService.ProblemCode, cancellationToken);
                        return await RestorePreviousAsync(before, release.Version, installService.ProblemCode, serviceInstalled, cancellationToken);
                    }
                    serviceInstalled = true;
                }

                await ReportStageAsync(stageReporter, "starting");
                var start = await privileged.RestartServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
                if (!start.Succeeded)
                {
                    await WriteDiagnosticAsync("warning", "Managed Mihomo service start failed: " + start.ProblemCode, cancellationToken);
                    return await RestorePreviousAsync(before, release.Version, start.ProblemCode, serviceInstalled, cancellationToken);
                }
                var health = await WaitForControllerReadinessAsync(cancellationToken);
                if (!health.Succeeded)
                {
                    await WriteDiagnosticAsync("warning", "Managed Mihomo service started but its loopback controller did not become ready: " + health.ProblemCode, cancellationToken);
                    return await RestorePreviousAsync(before, release.Version, string.IsNullOrEmpty(health.ProblemCode) ? ProxyProblemCodes.RuntimeHealthCheckFailed : health.ProblemCode, serviceInstalled, cancellationToken);
                }

                await WriteStateAsync(new RuntimeState(release.Version, before?.ActiveVersion, DateTimeOffset.UtcNow), cancellationToken);
                await WriteDiagnosticAsync("info", "Managed Mihomo installation completed successfully.", cancellationToken);
                await ReportStageAsync(stageReporter, "completed");
                return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Running, release.Version, before?.ActiveVersion, true, false);
            }
            catch (RuntimeInstallException exception) { await WriteDiagnosticAsync("warning", "Managed Mihomo installation failed: " + exception.ProblemCode, cancellationToken); return Failure(before, exception.ProblemCode); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { await WriteDiagnosticAsync("warning", "Managed Mihomo installation timed out during a runtime operation.", cancellationToken); return Failure(before, ProxyProblemCodes.RuntimeHealthCheckFailed); }
            catch (IOException) { await WriteDiagnosticAsync("warning", "Managed Mihomo installation encountered a file-system error.", cancellationToken); return Failure(before, ProxyProblemCodes.RuntimeIntegrityFailed); }
            catch (UnauthorizedAccessException) { await WriteDiagnosticAsync("warning", "Managed Mihomo installation was denied access to a protected host resource.", cancellationToken); return Failure(before, ProxyProblemCodes.PrivilegedOperationUnavailable); }
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyRuntimeDto> RollbackManagedAsync(string engineId, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id) return Unsupported(engineId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var before = await ReadStateAsync(cancellationToken);
            if (before?.PreviousVersion is not { Length: > 0 } previous || await probe.GetVersionAsync(ExecutablePath(previous), cancellationToken) is null)
                return Failure(before, ProxyProblemCodes.RuntimeHealthCheckFailed);
            var result = await privileged.ReplaceRuntimeAsync(new ReplaceProxyRuntimeOperation(MihomoEngine.Id, previous, ReleaseDirectoryId(previous)), cancellationToken);
            if (!result.Succeeded) return Failure(before, result.ProblemCode);
            result = await privileged.RestartServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            var health = await WaitForControllerReadinessAsync(cancellationToken);
            if (!result.Succeeded || !health.Succeeded) return Failure(before, result.Succeeded ? (string.IsNullOrEmpty(health.ProblemCode) ? ProxyProblemCodes.RuntimeHealthCheckFailed : health.ProblemCode) : result.ProblemCode);
            await WriteStateAsync(new RuntimeState(previous, before.ActiveVersion, DateTimeOffset.UtcNow), cancellationToken);
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Running, previous, before.ActiveVersion, true, false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyRuntimeDto> UninstallManagedAsync(string engineId, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id) return Unsupported(engineId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var before = await ReadStateAsync(cancellationToken);
            if (before is null) return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false, ProxyProblemCodes.RuntimeNotInstalled);
            var stop = await privileged.StopServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            if (!stop.Succeeded) return Failure(before, stop.ProblemCode);
            var service = await privileged.RemoveServiceAsync(new RemoveProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            if (!service.Succeeded) return Failure(before, service.ProblemCode);
            var runtime = await privileged.RemoveRuntimeAsync(new RemoveProxyRuntimeOperation(MihomoEngine.Id), cancellationToken);
            if (!runtime.Succeeded) return Failure(before, runtime.ProblemCode);
            DeleteState(); // Version material is removed by the constrained platform helper.
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false);
        }
        finally { _gate.Release(); }
    }

    private async Task StageAndInstallReleaseAsync(MihomoRuntimeRelease release, string finalDirectory, Func<MihomoRuntimeRelease, string, CancellationToken, Task> stageArchiveAsync, Func<string, Task>? stageReporter, CancellationToken cancellationToken)
    {
        // Directory.Move is atomic only within one filesystem. On Linux /tmp is commonly tmpfs
        // while the protected runtime location is under /var/lib, so staging under /tmp makes a
        // verified archive fail with EXDEV after extraction.
        var versionsDirectory = Path.GetDirectoryName(finalDirectory)!;
        var stagingRoot = Path.Combine(versionsDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(stagingRoot, "runtime." + release.ArchiveFormat);
        var staging = Path.Combine(stagingRoot, "release");
        try
        {
            Directory.CreateDirectory(versionsDirectory); MakePrivateDirectory(versionsDirectory);
            Directory.CreateDirectory(stagingRoot);
            await stageArchiveAsync(release, archive, cancellationToken);
            await ReportStageAsync(stageReporter, "verifying");
            await ReportStageAsync(stageReporter, "extracting");
            await ExtractExpectedBinaryAsync(release, archive, staging, cancellationToken);
            if (!HasExpectedArchitecture(Path.Combine(staging, BinaryName())) || await probe.GetVersionAsync(Path.Combine(staging, BinaryName()), cancellationToken) is null)
                throw new RuntimeInstallException(ProxyProblemCodes.RuntimeHealthCheckFailed);
            Directory.Move(staging, finalDirectory);
            MakePrivateDirectory(finalDirectory);
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private async Task DownloadAndVerifyAsync(MihomoRuntimeRelease release, string destination, CancellationToken cancellationToken)
    {
        using var response = await httpClients.CreateClient("MihomoRuntime").GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MihomoRuntimeManifest.MaximumArchiveBytes)
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await CopyAndVerifyArchiveAsync(release, input, destination, cancellationToken);
    }

    private async Task CopyAndVerifyArchiveAsync(MihomoRuntimeRelease release, string archivePath, string destination, CancellationToken cancellationToken)
    {
        if (!TrySafeArchivePath(archivePath, out var path))
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeArchiveUnavailable);

        FileInfo source;
        FileStream input;
        try
        {
            source = new FileInfo(path);
            if (source.Length > MihomoRuntimeManifest.MaximumArchiveBytes)
                throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
            input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (UnauthorizedAccessException)
        {
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeArchiveUnavailable);
        }
        catch (IOException)
        {
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeArchiveUnavailable);
        }
        await using (input)
            await CopyAndVerifyArchiveAsync(release, input, destination, cancellationToken);
    }

    private async Task CopyAndVerifyArchiveAsync(MihomoRuntimeRelease release, Stream input, string destination, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) break;
            total += read; if (total > MihomoRuntimeManifest.MaximumArchiveBytes) throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var matched = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(release.Sha256));
        await WriteDiagnosticAsync(matched ? "info" : "warning",
            $"Mihomo package SHA-256 verification {(matched ? "succeeded" : "failed")}: expected={release.Sha256.ToLowerInvariant()}; actual={actual}.", cancellationToken);
        if (!matched)
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
    }

    private static async Task ExtractExpectedBinaryAsync(MihomoRuntimeRelease release, string archivePath, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var target = Path.Combine(destination, BinaryName());
        if (release.ArchiveFormat == "zip")
        {
            using var archive = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry? binary = null;
            foreach (var entry in archive.Entries)
            {
                EnsureSafeArchiveEntry(entry.FullName, entry.Length);
                if (!entry.FullName.EndsWith("/", StringComparison.Ordinal) && IsExpectedArchiveBinary(Path.GetFileName(entry.FullName.Replace('\\', '/'))))
                {
                    if (binary is not null) throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
                    binary = entry;
                }
            }
            if (binary is null) throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
            await using var input = binary.Open(); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyLimitedAsync(input, output, cancellationToken);
        }
        else if (release.ArchiveFormat == "gz")
        {
            await using var compressed = File.OpenRead(archivePath);
            await using var input = new GZipStream(compressed, CompressionMode.Decompress);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyLimitedAsync(input, output, cancellationToken);
        }
        else throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
        MakePrivateExecutable(target);
    }

    private async Task<ProxyRuntimeDto> RestorePreviousAsync(RuntimeState? before, string attemptedVersion, string problemCode, bool serviceInstalled, CancellationToken cancellationToken)
    {
        await WriteDiagnosticAsync("warning", "Managed Mihomo installation is rolling back after: " + problemCode, cancellationToken);
        if (before?.ActiveVersion is { Length: > 0 } previous && File.Exists(ExecutablePath(previous)))
        {
            var replacement = await privileged.ReplaceRuntimeAsync(new ReplaceProxyRuntimeOperation(MihomoEngine.Id, previous, ReleaseDirectoryId(previous)), cancellationToken);
            if (!replacement.Succeeded)
            {
                await WriteDiagnosticAsync("error", "Managed Mihomo rollback could not restore the previous runtime: " + replacement.ProblemCode, cancellationToken);
                return Failure(before, ProxyProblemCodes.RecoveryRequired, attemptedVersion);
            }
            var restart = await privileged.RestartServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            var health = await WaitForControllerReadinessAsync(cancellationToken);
            if (!restart.Succeeded || !health.Succeeded)
            {
                await WriteDiagnosticAsync("error", "Managed Mihomo rollback could not restore a healthy service and controller.", cancellationToken);
                return Failure(before, ProxyProblemCodes.RecoveryRequired, attemptedVersion);
            }
        }
        else
        {
            if (serviceInstalled)
            {
                // A failed Windows service start can report "not active" when stopped.  The
                // removal result and runtime cleanup are authoritative for rollback success.
                await privileged.StopServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
                var service = await privileged.RemoveServiceAsync(new RemoveProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
                if (!service.Succeeded)
                {
                    await WriteDiagnosticAsync("error", "Managed Mihomo rollback could not remove the failed service: " + service.ProblemCode, cancellationToken);
                    return Failure(before, ProxyProblemCodes.RecoveryRequired, attemptedVersion);
                }
            }
            var runtime = await privileged.RemoveRuntimeAsync(new RemoveProxyRuntimeOperation(MihomoEngine.Id), cancellationToken);
            if (!runtime.Succeeded)
            {
                await WriteDiagnosticAsync("error", "Managed Mihomo rollback could not remove the failed runtime: " + runtime.ProblemCode, cancellationToken);
                return Failure(before, ProxyProblemCodes.RecoveryRequired, attemptedVersion);
            }
        }
        await WriteDiagnosticAsync("info", "Managed Mihomo rollback completed; the original failure remains: " + problemCode, cancellationToken);
        return Failure(before, problemCode, attemptedVersion);
    }

    private async Task<ProxyPrivilegedResult> EnsureBootstrapConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var directory = paths.GetProtectedConfigurationDirectory();
            Directory.CreateDirectory(directory); MakePrivateDirectory(directory);
            var secret = await controllerSecrets.GetOrCreateAsync(cancellationToken);
            var temporary = Path.Combine(directory, ".bootstrap-" + Guid.NewGuid().ToString("N"));
            var content = MihomoManagedConfiguration.WithServerControllerSettings(
                MihomoManagedConfiguration.WithServerGeoDataSettings("mixed-port: 7890\nmode: rule\nlog-level: warning\n"),
                controllerOptions, secret);
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            MakePrivateFile(temporary); File.Move(temporary, Path.Combine(directory, "active.yaml"), overwrite: true); MakePrivateFile(Path.Combine(directory, "active.yaml"));
            return new(true);
        }
        catch (ProxyControllerSecretException) { return new(false, ProxyProblemCodes.ConfigApplyFailed); }
        catch (ArgumentException) { return new(false, ProxyProblemCodes.ConfigInvalid); }
        catch (IOException) { return new(false, ProxyProblemCodes.PrivilegedOperationUnavailable); }
        catch (UnauthorizedAccessException) { return new(false, ProxyProblemCodes.PrivilegedOperationUnavailable); }
    }

    /// <summary>
    /// Starting a process or asking systemd to restart a service only means that its launch was
    /// accepted. Mihomo still needs a short interval to parse the bootstrap file and bind the
    /// loopback controller. Retrying transient connection failures avoids rolling a valid first
    /// install back merely because the very first probe won that race.
    /// </summary>
    private async Task<ControllerResult<bool>> WaitForControllerReadinessAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * controllerOptions.StartupReadinessSeconds);
        ControllerResult<bool> result;
        var attempts = 0;
        do
        {
            attempts++;
            result = await controller.IsReachableAsync(cancellationToken);
            if (result.Succeeded || result.ProblemCode is not (ProxyProblemCodes.ControllerUnavailable or ProxyProblemCodes.ControllerTimeout))
            {
                if (!result.Succeeded)
                    await WriteDiagnosticAsync("warning", "Managed Mihomo controller readiness failed after " + attempts + " check(s): " + result.ProblemCode, cancellationToken);
                return result;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                await WriteDiagnosticAsync("warning", "Managed Mihomo controller did not become reachable after " + attempts + " check(s): " + result.ProblemCode, cancellationToken);
                return result;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        while (Stopwatch.GetTimestamp() < deadline);

        await WriteDiagnosticAsync("warning", "Managed Mihomo controller readiness window elapsed after " + attempts + " check(s): " + result.ProblemCode, cancellationToken);
        return result;
    }

    private async Task<RuntimeState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        var path = StatePath(); if (!File.Exists(path)) return null;
        try { await using var input = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<RuntimeState>(input, cancellationToken: cancellationToken); }
        catch (JsonException) { return null; }
    }
    private async Task WriteStateAsync(RuntimeState value, CancellationToken cancellationToken)
    {
        var directory = paths.GetStateDirectory(); Directory.CreateDirectory(directory); MakePrivateDirectory(directory);
        var temporary = Path.Combine(directory, ".mihomo-state-" + Guid.NewGuid().ToString("N"));
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(output, value, cancellationToken: cancellationToken);
        MakePrivateFile(temporary); File.Move(temporary, StatePath(), overwrite: true); MakePrivateFile(StatePath());
    }
    private void DeleteState() { if (File.Exists(StatePath())) File.Delete(StatePath()); }
    private string StatePath() => Path.Combine(paths.GetStateDirectory(), "mihomo-runtime.json");
    private string VersionDirectory(string version) => Path.Combine(paths.GetEngineVersionsDirectory(MihomoEngine.Id), ReleaseDirectoryId(version));
    private string ExecutablePath(string version) => Path.Combine(VersionDirectory(version), BinaryName());
    private static string ReleaseDirectoryId(string version) => version + "-" + MihomoRuntimeManifest.CurrentRid();
    private static string BinaryName() => OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo";
    private static ProxyRuntimeDto Unsupported(string engineId) => new(engineId, ProxyRuntimeMode.None, ProxyRuntimeState.Failed, null, null, false, false, ProxyProblemCodes.NotSupported);
    private static ProxyRuntimeDto Failure(RuntimeState? state, string problemCode, string? version = null) => new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Failed, version ?? state?.ActiveVersion, state?.PreviousVersion, false, false, string.IsNullOrEmpty(problemCode) ? ProxyProblemCodes.RuntimeHealthCheckFailed : problemCode);
    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
    private static string ClassifyConfigurationValidationFailure(string output, bool timedOut)
    {
        var normalized = output.ToLowerInvariant();
        return normalized.Contains("can't initial geoip", StringComparison.Ordinal)
               || normalized.Contains("can't download mmdb", StringComparison.Ordinal)
               || normalized.Contains("geoip.metadb", StringComparison.Ordinal)
               || (normalized.Contains("geoip", StringComparison.Ordinal) && normalized.Contains("dns resolve failed", StringComparison.Ordinal))
            ? ProxyProblemCodes.GeodataUnavailable
            : timedOut ? ProxyProblemCodes.RuntimeHealthCheckFailed : ProxyProblemCodes.ConfigInvalid;
    }
    private static string ConfigurationValidationDiagnostic(string problemCode, bool timedOut) => problemCode == ProxyProblemCodes.GeodataUnavailable
        ? "Mihomo configuration validation requires GeoIP data, but the Server could not download it. Check DNS/outbound network access or remove GEOIP rules from the subscription."
        : timedOut
            ? "Mihomo configuration validation exceeded its 10-second limit."
            : "Mihomo rejected the downloaded subscription configuration.";
    private static bool TrySafeExternalPath(string value, out string path)
    {
        path = "";
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.GetFullPath(value);
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }
    private static bool TrySafeArchivePath(string value, out string path)
    {
        path = "";
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.GetFullPath(value);
            return File.Exists(path) && !Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }
    private static bool HasExpectedArchitecture(string path)
    {
        try
        {
            using var input = File.OpenRead(path); var header = new byte[512]; var count = input.Read(header, 0, header.Length);
            if (count < 64) return false;
            ushort machine;
            if (header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F') machine = BitConverter.ToUInt16(header, 18);
            else if (header[0] == (byte)'M' && header[1] == (byte)'Z')
            {
                var offset = BitConverter.ToInt32(header, 0x3c);
                if (offset < 0 || offset + 6 > count || header[offset] != (byte)'P' || header[offset + 1] != (byte)'E') return false;
                machine = BitConverter.ToUInt16(header, offset + 4);
            }
            else return false;
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => machine == 0x8664 || machine == 62,
                System.Runtime.InteropServices.Architecture.Arm64 => machine == 0xaa64 || machine == 183,
                _ => false,
            };
        }
        catch { return false; }
    }
    private static void EnsureSafeArchiveEntry(string name, long length)
    {
        var normal = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(name) || length < 0 || length > MihomoRuntimeManifest.MaximumArchiveBytes
            || Path.IsPathRooted(name) || normal.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
    }
    private static bool IsExpectedArchiveBinary(string fileName) => OperatingSystem.IsWindows()
        ? fileName.StartsWith("mihomo", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        : fileName.Equals(BinaryName(), StringComparison.OrdinalIgnoreCase);
    private static async Task CopyLimitedAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) return;
            total += read; if (total > MihomoRuntimeManifest.MaximumArchiveBytes) throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
    private static async Task ReportStageAsync(Func<string, Task>? stageReporter, string stage)
    {
        if (stageReporter is null) return;
        try { await stageReporter(stage); }
        catch { /* Progress reporting must never interrupt a runtime installation. */ }
    }
    private async Task WriteDiagnosticAsync(string level, string message, CancellationToken cancellationToken)
    {
        if (diagnostics is not null) await diagnostics.WriteAsync(level, message, cancellationToken);
    }
    private static void MakePrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void MakePrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    private static void MakePrivateExecutable(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private sealed record RuntimeState(string ActiveVersion, string? PreviousVersion, DateTimeOffset ActivatedAt);
}

public sealed class RuntimeInstallException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
