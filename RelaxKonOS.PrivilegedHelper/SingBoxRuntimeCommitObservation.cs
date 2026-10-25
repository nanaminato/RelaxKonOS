using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fresh read-only checks under the archive instance lock; saved receipts cannot hide physical drift.</summary>
internal static class SingBoxRuntimeCommitObservation
{
    internal static async Task<string?> ReadAsync(string root, RuntimeArchiveState state, ISingBoxArchiveHost host,
        CancellationToken ct, Func<string, RuntimeRelease, CancellationToken, Task<string>>? measure = null,
        RuntimeServiceSnapshot? expectedService = null)
    {
        try
        {
            var service = await host.ObserveAsync(ct);
            var configuration = Path.Combine(root, "config.json");
            RuntimeSourceStore.RequireNoLinks(configuration);
            if (state.ConfigurationIdentity is { } identity)
            {
                await using var file = new FileStream(configuration, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 4 * 1024 * 1024 || !Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(identity, StringComparison.OrdinalIgnoreCase))
                    return "runtime.configuration_drift";
            }
            else if (File.Exists(configuration) || Directory.Exists(configuration)) return "runtime.configuration_drift";
            if (state.Active is not { } active)
            {
                if (service.Exists) return "runtime.external_installation";
                var versions = Path.Combine(root, "versions"); RuntimeSourceStore.RequireNoLinks(versions);
                return Directory.Exists(versions) && Directory.EnumerateFileSystemEntries(versions).Any() ? "runtime.installed_file_drift" : null;
            }
            var revision = Path.Combine(root, "versions", active.RevisionId.ToString("N"));
            foreach (var entry in active.Release.Artifact.Layout.Files)
            {
                var path = Path.Combine(revision, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                RuntimeSourceStore.RequireNoLinks(path);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length != entry.Length || !Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return "runtime.installed_file_drift";
            }
            var binary = Path.Combine(revision, active.Release.Artifact.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
            if (!service.Exists || !service.Owned || service.BootEnabled is null || service.Executable is null
                || !string.Equals(Path.GetFullPath(service.Executable), Path.GetFullPath(binary),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return "runtime.state_unknown";
            if (service.Running && service.AppliedConfigurationIdentity != state.ConfigurationIdentity) return "runtime.configuration_drift";
            if (expectedService is not null && (service.Running != expectedService.Running || service.BootEnabled != expectedService.BootEnabled))
                return "runtime.state_unknown";
            measure ??= async (path, release, token) =>
            {
                token.ThrowIfCancellationRequested();
                var parsed = SingBoxNativeValidation.Parse(await SingBoxOperations.RunAsync(path, ["version"]), release.OriginalVersion,
                    release.Target.Os, release.Target.Architecture);
                token.ThrowIfCancellationRequested(); return parsed.Version;
            };
            return await measure(binary, active.Release, ct) == active.MeasuredVersion ? null : "runtime.native_version_mismatch";
        }
        catch (RuntimePolicyException ex) { return ex.ProblemCode; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return "runtime.state_unknown"; }
    }
}
