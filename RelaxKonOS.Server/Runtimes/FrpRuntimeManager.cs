using RelaxKonOS.Server.Installations;
using RelaxKonOS.Protocol.Installations;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Runtimes;

/// <summary>Owns RelaxKonOS-managed FRP releases. Activation changes a private state pointer, never overwrites a release.</summary>
public sealed class FrpRuntimeManager(IHostEnvironment environment, IHttpClientFactory httpClients, IOptions<FrpRuntimeOptions> options) : IRuntimeManager
{
    private const string RuntimeId = "frp";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data", "runtimes", RuntimeId);
    private readonly FrpRuntimeOptions _options = options.Value;

    public async Task<TunnelRuntimeDto> DetectExternalFrpcAsync(string executablePath, CancellationToken ct)
    {
        if (!TryCanonicalExternalPath(executablePath, out var path)) return Invalid("tunnel.external_path_invalid");
        if (!File.Exists(path)) return Invalid("tunnel.external_not_found");
        if (Directory.Exists(path)) return Invalid("tunnel.external_not_file");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) return Invalid("tunnel.external_not_executable");
        var version = await RunVersionAsync(path, ct);
        return version is null ? Invalid("tunnel.external_probe_failed", path) : new(RuntimeId, TunnelRuntimeMode.External, TunnelRuntimeState.Available, version, path);
    }

    public async Task<TunnelRuntimeDto> GetManagedFrpcStatusAsync(CancellationToken ct)
    {
        var state = await ReadStateAsync(ct);
        if (state?.ActiveVersion is not { Length: > 0 } active) return new(RuntimeId, TunnelRuntimeMode.Managed, TunnelRuntimeState.NotInstalled, null, null, "tunnel.managed_runtime_not_installed");
        var executable = ExecutablePath(active);
        return await RunVersionAsync(executable, ct) is not null
            ? new(RuntimeId, TunnelRuntimeMode.Managed, TunnelRuntimeState.Available, active, executable, "", null, state.PreviousVersion, true)
            : new(RuntimeId, TunnelRuntimeMode.Managed, TunnelRuntimeState.NotInstalled, active, null, "tunnel.managed_runtime_missing", null, state.PreviousVersion, false);
    }

    public async Task<TunnelRuntimeDto> GetManagedFrpsStatusAsync(CancellationToken ct)
    {
        var state = await ReadStateAsync(ct);
        if (state?.ActiveVersion is not { Length: > 0 } active) return new(RuntimeId, TunnelRuntimeMode.Managed, TunnelRuntimeState.NotInstalled, null, null, "tunnel.managed_runtime_not_installed");
        var executable = Path.Combine(VersionDirectory(active), FrpsName());
        return await RunVersionAsync(executable, ct) is not null
            ? new(RuntimeId, TunnelRuntimeMode.Managed, TunnelRuntimeState.Available, active, executable, "", null, state.PreviousVersion, true)
            : new(RuntimeId, TunnelRuntimeMode.Managed, TunnelRuntimeState.NotInstalled, active, null, "tunnel.managed_runtime_missing", null, state.PreviousVersion, false);
    }

    public Task<TunnelRuntimeDownloadDto?> GetManagedFrpcDownloadAsync(string version, CancellationToken ct)
    {
        var release = _options.Releases.SingleOrDefault(x => x.Version == version && x.Rid == CurrentRid());
        return Task.FromResult<TunnelRuntimeDownloadDto?>(IsTrustedRelease(release) ? new(release!.Version, release.Url) : null);
    }

    public Task<TunnelOperationResultDto> InstallManagedFrpcAsync(string version, IInstallationProgress progress, CancellationToken ct) =>
        InstallManagedFrpcCoreAsync(version, (release, destination, token) => DownloadVerifiedAsync(release, destination, progress, token), progress, ct);

    public Task<TunnelOperationResultDto> InstallManagedFrpcFromArchiveAsync(string version, Stream archive, long length, IInstallationProgress progress, CancellationToken ct) =>
        InstallManagedFrpcCoreAsync(version, (release, destination, token) => CopyVerifiedArchiveAsync(release, archive, destination, length, progress, token), progress, ct);

    private async Task<TunnelOperationResultDto> InstallManagedFrpcCoreAsync(string version,
        Func<FrpRuntimeRelease, string, CancellationToken, Task> stageArchiveAsync, IInstallationProgress progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 32)
        {

            return new(false, TunnelConnectionState.RuntimeUnavailable, "tunnel.runtime_version_invalid");
        }
        var release = _options.Releases.SingleOrDefault(x => x.Version == version && x.Rid == CurrentRid());
        if (!IsTrustedRelease(release))
        {

            return new(false, TunnelConnectionState.RuntimeUnavailable, "tunnel.runtime_release_not_configured");
        }

        await _gate.WaitAsync(ct);
        try
        {
            var finalDirectory = VersionDirectory(release!.Version);
            if (!Directory.Exists(finalDirectory))
            {
                Directory.CreateDirectory(_root); SetPrivateDirectory(_root);
                var staging = finalDirectory + ".installing-" + Guid.NewGuid().ToString("N");
                var archive = Path.Combine(_root, ".archive-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await stageArchiveAsync(release, archive, ct);
                    await progress.ReportAsync(new(InstallationStage.Extracting, Cancellable: true), ct);
                    await ExtractExpectedExecutablesAsync(release, archive, staging, ct);
                    await progress.ReportAsync(new(InstallationStage.HealthChecking, Cancellable: true), ct);
                    if (await RunVersionAsync(Path.Combine(staging, FrpcName()), ct) is null || await RunVersionAsync(Path.Combine(staging, FrpsName()), ct) is null) return CompleteInstallationFailure(release.Version, "tunnel.runtime_health_check_failed");
                    Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
                    Directory.Move(staging, finalDirectory);
                }
                catch (RuntimeInstallException ex) { return CompleteInstallationFailure(release.Version, ex.ProblemCode); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return CompleteInstallationFailure(release.Version, "tunnel.runtime_install_timeout"); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { return CompleteInstallationFailure(release.Version, "tunnel.runtime_install_failed"); }
                finally { if (File.Exists(archive)) File.Delete(archive); if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            }
            await progress.ReportAsync(new(InstallationStage.HealthChecking, Cancellable: true), ct);
            if (await RunVersionAsync(ExecutablePath(release.Version), ct) is null) return CompleteInstallationFailure(release.Version, "tunnel.runtime_health_check_failed");
            var before = await ReadStateAsync(ct);
            await progress.ReportAsync(new(InstallationStage.Activating, Cancellable: false), ct);
            await WriteStateAsync(new RuntimeState(release.Version, before?.ActiveVersion, DateTimeOffset.UtcNow), ct);

            return new(true, TunnelConnectionState.SavedNotApplied);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {

            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<TunnelOperationResultDto> RollbackManagedFrpcAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var before = await ReadStateAsync(ct);
            if (before?.PreviousVersion is not { Length: > 0 } previous) return new(false, TunnelConnectionState.RuntimeUnavailable, "tunnel.runtime_no_previous_version");
            if (await RunVersionAsync(ExecutablePath(previous), ct) is null) return new(false, TunnelConnectionState.RuntimeUnavailable, "tunnel.runtime_previous_unhealthy");
            await WriteStateAsync(new RuntimeState(previous, before.ActiveVersion, DateTimeOffset.UtcNow), ct);
            return new(true, TunnelConnectionState.SavedNotApplied);
        }
        finally { _gate.Release(); }
    }

    public async Task<TunnelOperationResultDto> UninstallManagedFrpcAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await ReadStateAsync(ct) is null)
                return new(false, TunnelConnectionState.RuntimeUnavailable, "tunnel.managed_runtime_not_installed");

            try
            {
                // Versions are private, immutable installation artifacts. Removing the runtime
                // intentionally removes the active pointer and every cached managed release.
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

                return new(true, TunnelConnectionState.SavedNotApplied);
            }
            catch (IOException) { return new(false, TunnelConnectionState.RuntimeUnavailable, "tunnel.runtime_uninstall_failed"); }
            catch (UnauthorizedAccessException) { return new(false, TunnelConnectionState.RuntimeUnavailable, "tunnel.runtime_uninstall_failed"); }
        }
        finally { _gate.Release(); }
    }

    private async Task DownloadVerifiedAsync(FrpRuntimeRelease release, string destination, IInstallationProgress progress, CancellationToken ct)
    {
        using var response = await httpClients.CreateClient("FrpRuntime").GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new RuntimeInstallException("tunnel.runtime_download_failed");
        if (response.Content.Headers.ContentLength > _options.MaximumArchiveBytes) throw new RuntimeInstallException("tunnel.runtime_download_too_large");
        await progress.ReportAsync(new(InstallationStage.Downloading, Cancellable: true), ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await CopyAndVerifyArchiveAsync(release, input, destination, response.Content.Headers.ContentLength, InstallationStage.Downloading, progress, ct);
    }

    private async Task CopyVerifiedArchiveAsync(FrpRuntimeRelease release, Stream input, string destination, long length, IInstallationProgress progress, CancellationToken ct)
    {
        if (length < 0 || length > _options.MaximumArchiveBytes) throw new RuntimeInstallException("tunnel.runtime_archive_too_large");
        await progress.ReportAsync(new(InstallationStage.Copying, 0, Cancellable: true), ct);
        await CopyAndVerifyArchiveAsync(release, input, destination, length, InstallationStage.Copying, progress, ct);
    }

    private async Task CopyAndVerifyArchiveAsync(FrpRuntimeRelease release, Stream input, string destination, long? length,
        InstallationStage transferStage, IInstallationProgress progress, CancellationToken ct)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct); if (count == 0) break;
            total += count; if (length is > 0 && total > length) throw new RuntimeInstallException("tunnel.runtime_length_invalid");
            if (total > _options.MaximumArchiveBytes) throw new RuntimeInstallException("tunnel.runtime_download_too_large");
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct);
            if (length is > 0)
                await progress.ReportAsync(new(transferStage, (int)Math.Min(100, total * 100 / length.Value), true), ct);
        }
        if (length is > 0 && total != length) throw new RuntimeInstallException("tunnel.runtime_length_invalid");
        await progress.ReportAsync(new(InstallationStage.Verifying, Cancellable: true), ct);
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(release.Sha256))) throw new RuntimeInstallException("tunnel.runtime_checksum_failed");
    }

    private static async Task ExtractExpectedExecutablesAsync(FrpRuntimeRelease release, string archivePath, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination); SetPrivateDirectory(destination); var found = new HashSet<string>(StringComparer.Ordinal);
        if (release.ArchiveFormat.Equals("zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries) { EnsureSafeEntry(entry.FullName, entry.Length); if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue; EnsureAllowedEntry(entry.FullName); await ExtractIfExpectedAsync(entry.FullName, entry.Open, destination, found, ct); }
        }
        else if (release.ArchiveFormat.Equals("tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            await using var input = File.OpenRead(archivePath); await using var gzip = new GZipStream(input, CompressionMode.Decompress); using var tar = new TarReader(gzip); TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                if (entry.EntryType is not TarEntryType.RegularFile) { if (!IsMetadataEntry(entry.Name)) throw new RuntimeInstallException("tunnel.runtime_archive_invalid"); continue; }
                EnsureSafeEntry(entry.Name, entry.Length); EnsureAllowedEntry(entry.Name); await ExtractIfExpectedAsync(entry.Name, () => entry.DataStream ?? Stream.Null, destination, found, ct);
            }
        }
        else throw new RuntimeInstallException("tunnel.runtime_archive_invalid");
        if (!found.Contains(FrpcName()) || !found.Contains(FrpsName())) throw new RuntimeInstallException("tunnel.runtime_archive_missing_binary");
    }

    private static async Task ExtractIfExpectedAsync(string name, Func<Stream> source, string destination, HashSet<string> found, CancellationToken ct)
    {
        var leaf = Path.GetFileName(name.Replace('\\', '/'));
        if (!string.Equals(leaf, FrpcName(), StringComparison.Ordinal) && !string.Equals(leaf, FrpsName(), StringComparison.Ordinal)) return;
        if (!found.Add(leaf)) throw new RuntimeInstallException("tunnel.runtime_archive_invalid");
        var target = Path.Combine(destination, leaf); await using var input = source(); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); await input.CopyToAsync(output, ct); SetPrivateExecutable(target);
    }

    private async Task<RuntimeState?> ReadStateAsync(CancellationToken ct) { var path = Path.Combine(_root, "state.json"); if (!File.Exists(path)) return null; try { await using var input = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<RuntimeState>(input, cancellationToken: ct); } catch (JsonException) { throw new RuntimeInstallException("tunnel.runtime_state_invalid"); } }
    private async Task WriteStateAsync(RuntimeState value, CancellationToken ct) { Directory.CreateDirectory(_root); SetPrivateDirectory(_root); var temporary = Path.Combine(_root, ".state-" + Guid.NewGuid().ToString("N")); var path = Path.Combine(_root, "state.json"); await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, value, cancellationToken: ct); SetPrivateFile(temporary); File.Move(temporary, path, overwrite: true); SetPrivateFile(path); }
    private static bool TryCanonicalExternalPath(string value, out string path) { path = ""; try { if (!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)) { path = Path.GetFullPath(value); return true; } } catch { } return false; }
    private static bool IsTrustedRelease(FrpRuntimeRelease? value) => value is not null && Path.GetFileName(value.Version) == value.Version && value.Version.StartsWith("v", StringComparison.Ordinal) && value.ArchiveFormat is "zip" or "tar.gz" && value.Sha256.Length == 64 && value.Sha256.All(Uri.IsHexDigit) && Uri.TryCreate(value.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/fatedier/frp/releases/download/", StringComparison.Ordinal);
    private static void EnsureSafeEntry(string? name, long length) { if (string.IsNullOrWhiteSpace(name) || length < 0 || length > 128L * 1024 * 1024 || Path.IsPathRooted(name) || name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Any(x => x is "." or "..")) throw new RuntimeInstallException("tunnel.runtime_archive_invalid"); }
    private static void EnsureAllowedEntry(string name) { var leaf = Path.GetFileName(name.Replace('\\', '/')); if (string.Equals(leaf, FrpcName(), StringComparison.Ordinal) || string.Equals(leaf, FrpsName(), StringComparison.Ordinal) || leaf.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || leaf.Equals("frpc.toml", StringComparison.OrdinalIgnoreCase) || leaf.Equals("frps.toml", StringComparison.OrdinalIgnoreCase)) return; throw new RuntimeInstallException("tunnel.runtime_archive_unexpected_entry"); }
    private static bool IsMetadataEntry(string? name) => string.IsNullOrEmpty(name) || name.EndsWith("/", StringComparison.Ordinal);
    private string VersionDirectory(string version) => Path.Combine(_root, "versions", version + "-" + CurrentRid());
    private string ExecutablePath(string version) => Path.Combine(VersionDirectory(version), FrpcName());
    private static string CurrentRid() => OperatingSystem.IsWindows() ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64") : (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64");
    private static string FrpcName() => OperatingSystem.IsWindows() ? "frpc.exe" : "frpc";
    private static string FrpsName() => OperatingSystem.IsWindows() ? "frps.exe" : "frps";
    private static async Task<string?> RunVersionAsync(string executable, CancellationToken ct)
    {
        if (!File.Exists(executable)) return null;
        using var p = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        p.StartInfo.ArgumentList.Add("--version");
        try
        {
            if (!p.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = p.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, p.WaitForExitAsync(timeout.Token));
            var value = await output;
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(value) ? value.Trim()[..Math.Min(128, value.Trim().Length)] : null;
        }
        catch { return null; }
    }
    private static TunnelRuntimeDto Invalid(string code, string? path = null) => new(RuntimeId, TunnelRuntimeMode.External, TunnelRuntimeState.ExternalInvalid, null, path, code);
    private TunnelOperationResultDto CompleteInstallationFailure(string version, string problemCode)
    {

        return new(false, TunnelConnectionState.RuntimeUnavailable, problemCode);
    }
    private static void SetPrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void SetPrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    private static void SetPrivateExecutable(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private sealed record RuntimeState(string ActiveVersion, string? PreviousVersion, DateTimeOffset ActivatedAt);
}

public sealed class RuntimeInstallException(string problemCode) : Exception(problemCode) { public string ProblemCode { get; } = problemCode; }
