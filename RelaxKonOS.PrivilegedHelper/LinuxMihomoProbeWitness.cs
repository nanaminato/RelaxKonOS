using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Host-owned registration and observation of one fixed, unprivileged probe worker.</summary>
[SupportedOSPlatform("linux")]
internal static class LinuxMihomoProbeWitness
{
    internal const string WorkerArgument = "--mihomo-version-worker";
    private const string Worker = "/usr/local/lib/relaxkonos/privileged-helper/RelaxKonOS.PrivilegedHelper";
    private const string Root = "/var/lib/relaxkonos/mihomo-probe-witnesses";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    private sealed record Dispatch(Guid OperationId, uint OwnerUid, Guid BootId, Guid InvocationId, int Pid,
        ulong StartTicks, ulong StartedAt);

    internal static bool HardenWorker() =>
        new LinuxUnixSocketFileSystem().EffectiveUserId != 0
        && File.ReadLines("/proc/self/status").Contains("NoNewPrivs:\t1", StringComparer.Ordinal)
        && Prctl(4, 0, 0, 0, 0) == 0; // PR_SET_DUMPABLE

    internal static async Task<int> RunCommandAsync(string action, string operation, string uid)
    {
        var fileSystem = new LinuxUnixSocketFileSystem();
        if (fileSystem.EffectiveUserId != 0) return 77;
        if (action is not ("register" or "read") || !Guid.TryParseExact(operation, "D", out var id) || id == Guid.Empty
            || !uint.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var owner) || owner == 0)
            return 64;
        try
        {
            RuntimeSourceStore.RequireNoLinks(Root);
            var parent = Path.GetDirectoryName(Root)!;
            RequireProtected(fileSystem, parent, directory: true);
            var path = Path.Combine(Root, id.ToString("N") + ".json");
            if (action == "register")
            {
                Directory.CreateDirectory(Root, PrivateDirectory);
                RequireProtected(fileSystem, Root, directory: true, mode: PrivateDirectory);
                if (Path.Exists(path)) return 75;
                var dispatch = await CaptureAsync(fileSystem, id, owner);
                using var output = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile
                });
                JsonSerializer.Serialize(output, dispatch);
                output.Flush(flushToDisk: true);
                LinuxDirectorySync.Flush(Root);
                LinuxDirectorySync.Flush(parent);
                return 0;
            }

            RequireProtected(fileSystem, Root, directory: true, mode: PrivateDirectory);
            RequireProtected(fileSystem, path, directory: false, mode: PrivateFile);
            using var input = File.OpenRead(path);
            var registered = input.Length <= 1024 ? JsonSerializer.Deserialize<Dispatch>(input, StrictJson) : null;
            if (registered is null || registered.OperationId != id || registered.OwnerUid != owner
                || registered.BootId != LinuxMihomoTunCheckpoint.BootId() || registered.InvocationId == Guid.Empty
                || registered.Pid <= 1 || registered.StartTicks == 0 || registered.StartedAt == 0
                || registered.StartedAt > MonotonicMicroseconds() || ReadStartTicks(registered.Pid) == registered.StartTicks
                || !await HasSuccessfulExitAsync(registered))
                return 75;
            Console.WriteLine(JsonSerializer.Serialize(registered));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException or InvalidOperationException or RuntimePolicyException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            return 75;
        }
    }

    private static async Task<Dispatch> CaptureAsync(LinuxUnixSocketFileSystem fileSystem, Guid id, uint owner)
    {
        RuntimeSourceStore.RequireNoLinks(Worker);
        RequireProtected(fileSystem, Worker, directory: false);
        for (var directory = Path.GetDirectoryName(Worker); directory is not null; directory = Path.GetDirectoryName(directory))
            RequireProtected(fileSystem, directory, directory: true);
        var state = await ReadStateAsync(id);
        if (state.GetValueOrDefault("RemainAfterExit") != "yes"
            || state.GetValueOrDefault("MainPID") != state.GetValueOrDefault("ExecMainPID")
            || !int.TryParse(state.GetValueOrDefault("MainPID"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 1 || !Guid.TryParseExact(state.GetValueOrDefault("InvocationID"), "N", out var invocation)
            || invocation == Guid.Empty
            || !ulong.TryParse(state.GetValueOrDefault("ExecMainStartTimestampMonotonic"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var started) || started == 0 || started > MonotonicMicroseconds())
            throw new IOException("The original version worker is unavailable.");

        var ticks = ReadStartTicks(pid);
        var proc = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}";
        var status = File.ReadAllLines(proc + "/status");
        var uid = owner.ToString(CultureInfo.InvariantCulture);
        if (ticks is null || new FileInfo(proc + "/exe").LinkTarget != Worker
            || await File.ReadAllTextAsync(proc + "/cmdline") != $"{Worker}\0{WorkerArgument}\0{id:D}\0"
            || !status.Contains($"Uid:\t{uid}\t{uid}\t{uid}\t{uid}", StringComparer.Ordinal)
            || !status.Contains("NoNewPrivs:\t1", StringComparer.Ordinal)
            || !status.Contains("CapPrm:\t0000000000000000", StringComparer.Ordinal)
            || !status.Contains("CapEff:\t0000000000000000", StringComparer.Ordinal))
            throw new IOException("The original version worker has an unexpected identity.");

        var observed = await ReadStateAsync(id);
        if (state.Count != observed.Count || state.Any(field => observed.GetValueOrDefault(field.Key) != field.Value)
            || ReadStartTicks(pid) != ticks)
            throw new IOException("The original version worker changed during registration.");
        return new(id, owner, LinuxMihomoTunCheckpoint.BootId(), invocation, pid, ticks.Value, started);
    }

    private static async Task<Dictionary<string, string>> ReadStateAsync(Guid id)
    {
        var state = await NativeRuntimeProcess.RunAsync("/usr/bin/systemctl",
            ["show", "--property=ActiveState,SubState,RemainAfterExit,MainPID,ExecMainPID,InvocationID,"
                + "ExecMainStartTimestampMonotonic,ExecMainExitTimestampMonotonic,ExecMainCode,ExecMainStatus", "--", Unit(id)], 4096);
        return state.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).ToDictionary(fields => fields[0], fields => fields.Length == 2 ? fields[1] : "");
    }

    private static async Task<bool> HasSuccessfulExitAsync(Dispatch dispatch)
    {
        var state = await ReadStateAsync(dispatch.OperationId);
        // A service can be classified as successful after some signals. Require CLD_EXITED and exit code zero.
        return state.GetValueOrDefault("RemainAfterExit") == "yes"
            && state.GetValueOrDefault("ActiveState") == "active" && state.GetValueOrDefault("SubState") == "exited"
            && state.GetValueOrDefault("MainPID") == "0" && state.GetValueOrDefault("ExecMainCode") == "1"
            && state.GetValueOrDefault("ExecMainStatus") == "0"
            && state.GetValueOrDefault("ExecMainPID") == dispatch.Pid.ToString(CultureInfo.InvariantCulture)
            && state.GetValueOrDefault("InvocationID") == dispatch.InvocationId.ToString("N")
            && state.GetValueOrDefault("ExecMainStartTimestampMonotonic") == dispatch.StartedAt.ToString(CultureInfo.InvariantCulture)
            && ulong.TryParse(state.GetValueOrDefault("ExecMainExitTimestampMonotonic"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var ended) && ended >= dispatch.StartedAt && ended <= MonotonicMicroseconds();
    }

    private static ulong? ReadStartTicks(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            var end = stat.LastIndexOf(')');
            if (end < 0) throw new IOException("The version worker process identity is invalid.");
            var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 20 && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks != 0
                ? ticks : throw new IOException("The version worker process start is unavailable.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void RequireProtected(LinuxUnixSocketFileSystem fileSystem, string path, bool directory, UnixFileMode? mode = null)
    {
        if (fileSystem.Stat(path) is not { OwnerId: 0, IsSymbolicLink: false, IsSocket: false } status
            || status.IsDirectory != directory || (status.Mode & OtherWrite) != 0
            || (mode is { } expected && status.Mode != expected))
            throw new IOException("The probe witness path is not protected.");
    }

    private static string Unit(Guid id) => $"rk-mihomo-probe-{id:N}.service";
    private static ulong MonotonicMicroseconds() => (ulong)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);

    [DllImport("libc", EntryPoint = "prctl")]
    private static extern int Prctl(int operation, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}
