using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;

internal static class MihomoDataChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Protect(string path)
        {
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var work = Path.Combine(Path.GetTempPath(), "mihomo-data-" + Guid.NewGuid().ToString("N"));
        Protect(work);
        var source = Path.Combine(work, "source"); Protect(source);
        var target = Path.Combine(work, "target");
        async Task Reject(string code, Func<Task> action)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected data refusal: " + code);
        }
        try
        {
            Protect(Path.Combine(source, "nested"));
            await File.WriteAllTextAsync(Path.Combine(source, "nested", "state.db"), "private-state");
            await File.WriteAllTextAsync(Path.Combine(source, "empty"), "");
            await MihomoRuntimeData.CopyAsync(source, target, Protect, default);
            Check(await File.ReadAllTextAsync(Path.Combine(target, "nested", "state.db")) == "private-state"
                && new FileInfo(Path.Combine(target, "empty")).Length == 0, "Private data tree and empty files copied");
            await File.WriteAllTextAsync(Path.Combine(target, "nested", "state.db"), "target-writes");
            Check(await File.ReadAllTextAsync(Path.Combine(source, "nested", "state.db")) == "private-state", "Data revisions do not share writable files");
            if (!OperatingSystem.IsWindows())
                Check((File.GetUnixFileMode(Path.Combine(target, "nested", "state.db"))
                    & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                    "Copied data files remain private");
            await Reject("staging_conflict", () => MihomoRuntimeData.CopyAsync(source, target, Protect, default));
            await Reject("data_scope_invalid", () => MihomoRuntimeData.CopyAsync(source, source, Protect, default));
            await Reject("data_scope_invalid", () => MihomoRuntimeData.CopyAsync(source, Path.Combine(source, "inside"), Protect, default));
            await Reject("data_snapshot_missing", () => MihomoRuntimeData.CopyAsync(Path.Combine(work, "missing"), Path.Combine(work, "missing-copy"), Protect, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                await MihomoRuntimeData.CopyAsync(source, Path.Combine(work, "cancelled"), Protect, cancelled.Token);
                throw new Exception("Cancelled data copy succeeded");
            }
            catch (OperationCanceledException) { count++; }
            if (OperatingSystem.IsLinux())
            {
                var link = Path.Combine(source, "escaped");
                File.CreateSymbolicLink(link, Path.Combine(work, "target", "empty"));
                // Link rejection has the shared path policy's problem code, independently of file content.
                try
                {
                    await MihomoRuntimeData.CopyAsync(source, Path.Combine(work, "linked"), Protect, default);
                    throw new Exception("Linked data file was copied");
                }
                catch (RuntimePolicyException) { count++; }
                File.Delete(link);
                var pipe = Path.Combine(source, "pipe");
                await NativeRuntimeProcess.RunAsync("/usr/bin/mkfifo", [pipe], 1024);
                await Reject("data_file_unsupported", () => MihomoRuntimeData.CopyAsync(source, Path.Combine(work, "fifo"), Protect, default));
                File.Delete(pipe);
                var huge = Path.Combine(source, "huge");
                using (var output = File.Create(huge)) output.SetLength(MihomoRuntimeData.MaximumBytes + 1);
                await Reject("data_snapshot_too_large", () => MihomoRuntimeData.CopyAsync(source, Path.Combine(work, "too-large"), Protect, default));
            }
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(work)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Data test cleanup escaped its temporary root");
            Directory.Delete(work, true);
        }
        return count;
    }
}
