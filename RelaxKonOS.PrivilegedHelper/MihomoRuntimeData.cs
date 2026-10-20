using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Bounded private data revisions. The caller stops the owned service before copying mutable files;
/// old data is retained untouched for compensation and explicit rollback. No links or special files.
/// </summary>
internal static class MihomoRuntimeData
{
    internal const long MaximumBytes = 1024L * 1024 * 1024;
    private const int MaximumEntries = 4096;

    internal static async Task RequireRegularFileAsync(string path, string problemCode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RuntimeSourceStore.RequireNoLinks(path);
        if (OperatingSystem.IsLinux())
        {
            var kind = (await NativeRuntimeProcess.RunAsync("/usr/bin/stat", ["-c", "%F", "--", path], 128)).Trim();
            if (kind is not ("regular file" or "regular empty file")) throw new RuntimePolicyException(problemCode);
        }
        else if ((File.GetAttributes(path) & FileAttributes.Device) != 0) throw new RuntimePolicyException(problemCode);
        ct.ThrowIfCancellationRequested();
    }

    internal static async Task RetainAsync(string root, RuntimeStoredRevision active, CancellationToken ct)
    {
        var path = Path.Combine(root, "retained-data.json");
        RuntimeSourceStore.RequireNoLinks(path);
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".retained");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
            { await output.WriteAsync(RuntimeJson.Serialize(active), ct); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    internal static string? RetainedSource(string root, RuntimeRelease target)
    {
        var path = Path.Combine(root, "retained-data.json");
        RuntimeSourceStore.RequireNoLinks(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new RuntimePolicyException("runtime.data_snapshot_invalid");
        var retained = RuntimeJson.Read<RuntimeStoredRevision>(File.ReadAllBytes(path));
        if (retained.RevisionId == Guid.Empty || retained.Release.RuntimeId != "mihomo"
            || retained.Release.ConfigurationCapability != target.ConfigurationCapability
            || !RuntimeSemanticVersion.TryParse(retained.MeasuredVersion, out _))
            throw new RuntimePolicyException("runtime.data_snapshot_invalid");
        if (MihomoNativeValidation.UpdateProblem(retained.MeasuredVersion, target.OriginalVersion) == "runtime.update_target_not_newer")
            throw new RuntimePolicyException("runtime.retained_data_version_unsupported");
        var data = Path.Combine(root, "data", retained.RevisionId.ToString("N"));
        RuntimeSourceStore.RequireNoLinks(data);
        if (!Directory.Exists(data)) throw new RuntimePolicyException("runtime.data_snapshot_missing");
        return data;
    }

    internal static async Task CopyAsync(string source, string destination, Action<string> protect, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (source == destination || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new RuntimePolicyException("runtime.data_scope_invalid");
        RuntimeSourceStore.RequireNoLinks(source);
        RuntimeSourceStore.RequireNoLinks(destination);
        if (!Directory.Exists(source)) throw new RuntimePolicyException("runtime.data_snapshot_missing");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new RuntimePolicyException("runtime.staging_conflict");
        protect(destination);
        long total = 0;
        var entries = 0;
        await CopyDirectoryAsync(source, destination, 0);

        async Task CopyDirectoryAsync(string from, string to, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 32) throw new RuntimePolicyException("runtime.data_snapshot_too_large");
            RuntimeSourceStore.RequireNoLinks(from);
            RuntimeSourceStore.RequireNoLinks(to);
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaximumEntries) throw new RuntimePolicyException("runtime.data_snapshot_too_large");
                RuntimeSourceStore.RequireNoLinks(entry);
                var target = Path.Combine(to, Path.GetFileName(entry));
                RuntimeSourceStore.RequireNoLinks(target);
                if (Directory.Exists(entry))
                {
                    protect(target);
                    await CopyDirectoryAsync(entry, target, depth + 1);
                    continue;
                }
                await RequireRegularFileAsync(entry, "runtime.data_file_unsupported", ct);
                await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > MaximumBytes - total) throw new RuntimePolicyException("runtime.data_snapshot_too_large");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var output = new FileStream(target, options);
                var buffer = new byte[65536];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (read > MaximumBytes - total) throw new RuntimePolicyException("runtime.data_snapshot_too_large");
                    total += read;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                output.Flush(true);
            }
        }
    }
}
