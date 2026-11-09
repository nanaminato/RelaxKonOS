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
    private const string CgroupRoot = "/sys/fs/cgroup";
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
        ulong StartTicks, ulong StartedAt, string ControlGroup, ulong ControlGroupInode, ulong ControlGroupMountId);

    internal static bool HardenWorker() =>
        new LinuxUnixSocketFileSystem().EffectiveUserId != 0
        && File.ReadLines("/proc/self/status").Contains("NoNewPrivs:\t1", StringComparer.Ordinal)
        && Prctl(4, 0, 0, 0, 0) == 0; // PR_SET_DUMPABLE

    private sealed record Terminal(Dispatch Dispatch, ulong ExitedAt);

    internal static async Task<int> RunCommandAsync(string action, string operation, string uid)
    {
        var fileSystem = new LinuxUnixSocketFileSystem();
        if (fileSystem.EffectiveUserId != 0) return 77;
        if (action is not ("register" or "seal" or "read") || !Guid.TryParseExact(operation, "D", out var id) || id == Guid.Empty
            || !uint.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var owner) || owner == 0)
            return 64;
        var result = await ExecuteAsync(action, id, owner);
        if (result.MihomoProbeExit is { } exit) Console.WriteLine(JsonSerializer.Serialize(exit));
        return result.ExitCode;
    }

    internal static Task<PrivilegedOperationResult> HandleAuthenticatedAsync(PrivilegedOperationRequest request, HelperPeerIdentity peer)
    {
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, Version: request.Version);
        if (request != clean || request.OperationId is not { } id || id == Guid.Empty)
            return Task.FromResult(new PrivilegedOperationResult(false, 64, ProblemCode: PrivilegedProblemCode.InvalidRequest));
        if (!peer.Allowed || peer.UserId == 0)
            return Task.FromResult(new PrivilegedOperationResult(false, 77, ProblemCode: PrivilegedProblemCode.AccessDenied));
        return ExecuteAsync(request.Operation == PrivilegedOperationKind.MihomoProbeWitnessRegister ? "register" : "read", id, peer.UserId);
    }

    private static async Task<PrivilegedOperationResult> ExecuteAsync(string action, Guid id, uint owner)
    {
        var fileSystem = new LinuxUnixSocketFileSystem();
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
                if (Path.Exists(path)) return Unknown();
                var dispatch = await CaptureAsync(fileSystem, id, owner);
                WriteNew(path, dispatch);
                LinuxDirectorySync.Flush(parent);
                return new(true);
            }

            RequireProtected(fileSystem, Root, directory: true, mode: PrivateDirectory);
            var registered = Read<Dispatch>(fileSystem, path);
            if (registered is null || registered.OperationId != id || registered.OwnerUid != owner
                || registered.BootId != LinuxMihomoTunCheckpoint.BootId() || registered.InvocationId == Guid.Empty
                || registered.ControlGroup != ControlGroup(id) || registered.ControlGroupInode == 0 || registered.ControlGroupMountId == 0
                || registered.Pid <= 1 || registered.StartTicks == 0 || registered.StartedAt == 0
                || registered.StartedAt > MonotonicMicroseconds())
                return Unknown();

            var terminalPath = Path.Combine(Root, id.ToString("N") + ".exit.json");
            if (action == "seal" && !Path.Exists(terminalPath))
            {
                if (ReadStartTicks(registered.Pid) == registered.StartTicks || await ReadExitTimeAsync(fileSystem, registered) is not { } ended)
                    return Unknown();
                WriteNew(terminalPath, new Terminal(registered, ended));
            }
            var terminal = Read<Terminal>(fileSystem, terminalPath);
            if (terminal is null || terminal.Dispatch != registered || terminal.ExitedAt < registered.StartedAt
                || terminal.ExitedAt > MonotonicMicroseconds())
                return Unknown();
            // A read racing the original publication still requires the directory entry to be durable.
            LinuxDirectorySync.Flush(Root);
            return new(true, MihomoProbeExit: new(id, registered.BootId, registered.InvocationId, registered.Pid,
                registered.StartTicks, registered.StartedAt, terminal.ExitedAt));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException or InvalidOperationException or RuntimePolicyException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            return Unknown();
        }
    }

    private static T? Read<T>(LinuxUnixSocketFileSystem fileSystem, string path) where T : class
    {
        RequireProtected(fileSystem, path, directory: false, mode: PrivateFile);
        using var input = File.OpenRead(path);
        return input.Length <= 1024 ? JsonSerializer.Deserialize<T>(input, StrictJson) : null;
    }

    private static void WriteNew<T>(string path, T record)
    {
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(staging, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile
            }))
            {
                JsonSerializer.Serialize(output, record);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: false);
            LinuxDirectorySync.Flush(Root);
        }
        finally { File.Delete(staging); }
    }

    private static PrivilegedOperationResult Unknown() =>
        new(false, 75, ProblemCode: PrivilegedProblemCode.RecoveryRequired);

    private static async Task<Dispatch> CaptureAsync(LinuxUnixSocketFileSystem fileSystem, Guid id, uint owner)
    {
        RuntimeSourceStore.RequireNoLinks(Worker);
        RequireProtected(fileSystem, Worker, directory: false);
        for (var directory = Path.GetDirectoryName(Worker); directory is not null; directory = Path.GetDirectoryName(directory))
            RequireProtected(fileSystem, directory, directory: true);
        var state = await ReadStateAsync(id);
        if (!HasFixedScopePolicy(state) || state.GetValueOrDefault("ControlGroup") != ControlGroup(id)
            || state.GetValueOrDefault("MainPID") != state.GetValueOrDefault("ExecMainPID")
            || !int.TryParse(state.GetValueOrDefault("MainPID"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 1 || !Guid.TryParseExact(state.GetValueOrDefault("InvocationID"), "N", out var invocation)
            || invocation == Guid.Empty
            || !ulong.TryParse(state.GetValueOrDefault("ExecMainStartTimestampMonotonic"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var started) || started == 0 || started > MonotonicMicroseconds())
            throw new IOException("The original version worker is unavailable.");

        var group = ControlGroup(id);
        var scope = ControlGroupDirectory(group);
        if (fileSystem.Stat(scope) is not { IsDirectory: true, OwnerId: 0, Inode: > 0, MountId: > 0 } scopeStatus
            || (scopeStatus.Mode & OtherWrite) != 0 || fileSystem.Stat(CgroupRoot)?.MountId != scopeStatus.MountId)
            throw new IOException("The original probe cgroup identity is unavailable.");
        var ticks = ReadStartTicks(pid);
        var proc = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}";
        var status = File.ReadAllLines(proc + "/status");
        var uid = owner.ToString(CultureInfo.InvariantCulture);
        if (ticks is null || new FileInfo(proc + "/exe").LinkTarget != Worker
            || await File.ReadAllTextAsync(proc + "/cmdline") != $"{Worker}\0{WorkerArgument}\0{id:D}\0"
            || !File.ReadLines(proc + "/cgroup").SequenceEqual([$"0::{group}"], StringComparer.Ordinal)
            || !status.Contains($"Uid:\t{uid}\t{uid}\t{uid}\t{uid}", StringComparer.Ordinal)
            || !status.Contains("NoNewPrivs:\t1", StringComparer.Ordinal)
            || !status.Contains("CapPrm:\t0000000000000000", StringComparer.Ordinal)
            || !status.Contains("CapEff:\t0000000000000000", StringComparer.Ordinal))
            throw new IOException("The original version worker has an unexpected identity.");

        var observed = await ReadStateAsync(id);
        if (state.Count != observed.Count || state.Any(field => observed.GetValueOrDefault(field.Key) != field.Value)
            || ReadStartTicks(pid) != ticks || fileSystem.Stat(scope) != scopeStatus)
            throw new IOException("The original version worker changed during registration.");
        return new(id, owner, LinuxMihomoTunCheckpoint.BootId(), invocation, pid, ticks.Value, started, group, scopeStatus.Inode, scopeStatus.MountId);
    }

    private static async Task<Dictionary<string, string>> ReadStateAsync(Guid id)
    {
        var state = await NativeRuntimeProcess.RunAsync("/usr/bin/systemctl",
            ["show", "--property=ActiveState,SubState,RemainAfterExit,MainPID,ExecMainPID,InvocationID,"
                + "Type,ExitType,ControlGroup,Delegate,ProtectControlGroups,KillMode,Restart,"
                + "ExecMainStartTimestampMonotonic,ExecMainExitTimestampMonotonic,ExecMainCode,ExecMainStatus", "--", Unit(id)], 4096);
        return state.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).ToDictionary(fields => fields[0], fields => fields.Length == 2 ? fields[1] : "");
    }

    private static async Task<ulong?> ReadExitTimeAsync(LinuxUnixSocketFileSystem fileSystem, Dispatch dispatch)
    {
        var state = await ReadStateAsync(dispatch.OperationId);
        var phase = state.GetValueOrDefault("SubState");
        var group = state.GetValueOrDefault("ControlGroup");
        // A service can be classified as successful after some signals. Require CLD_EXITED and exit code zero.
        return HasFixedScopePolicy(state)
            && state.GetValueOrDefault("ActiveState") == "active" && phase is ("running" or "exited")
            && (group == dispatch.ControlGroup || (phase == "exited" && (group is null or "")))
            && state.GetValueOrDefault("MainPID") == "0" && state.GetValueOrDefault("ExecMainCode") == "1"
            && state.GetValueOrDefault("ExecMainStatus") == "0"
            && state.GetValueOrDefault("ExecMainPID") == dispatch.Pid.ToString(CultureInfo.InvariantCulture)
            && state.GetValueOrDefault("InvocationID") == dispatch.InvocationId.ToString("N")
            && state.GetValueOrDefault("ExecMainStartTimestampMonotonic") == dispatch.StartedAt.ToString(CultureInfo.InvariantCulture)
            && ulong.TryParse(state.GetValueOrDefault("ExecMainExitTimestampMonotonic"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var ended) && ended >= dispatch.StartedAt && ended <= MonotonicMicroseconds()
            && IsOriginalScopeEmpty(fileSystem, dispatch, phase == "exited") ? ended : null;
    }

    private static bool IsOriginalScopeEmpty(LinuxUnixSocketFileSystem fileSystem, Dispatch dispatch, bool managerExited)
    {
        if (fileSystem.Stat(CgroupRoot)?.MountId != dispatch.ControlGroupMountId) return false;
        var scope = ControlGroupDirectory(dispatch.ControlGroup);
        var observed = fileSystem.Stat(scope);
        if (observed is null)
        {
            if (!managerExited) return false;
            try { _ = File.GetAttributes(scope); return false; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
        }
        if (observed is not { IsDirectory: true, OwnerId: 0 } status || status.Inode != dispatch.ControlGroupInode
            || status.MountId != dispatch.ControlGroupMountId
            || (status.Mode & OtherWrite) != 0)
            return false;
        var population = File.ReadLines(Path.Combine(scope, "cgroup.events"))
            .Where(line => line.StartsWith("populated ", StringComparison.Ordinal));
        return population.SequenceEqual(["populated 0"], StringComparer.Ordinal) && fileSystem.Stat(scope) == observed;
    }

    private static bool HasFixedScopePolicy(Dictionary<string, string> state) =>
        state.GetValueOrDefault("Type") == "exec" && state.GetValueOrDefault("ExitType") == "cgroup"
        && state.GetValueOrDefault("RemainAfterExit") == "yes" && state.GetValueOrDefault("Delegate") == "no"
        && state.GetValueOrDefault("ProtectControlGroups") == "yes"
        && state.GetValueOrDefault("KillMode") == "control-group" && state.GetValueOrDefault("Restart") == "no";

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
    private static string ControlGroup(Guid id) => "/system.slice/" + Unit(id);
    private static string ControlGroupDirectory(string group) => CgroupRoot + group;
    private static ulong MonotonicMicroseconds() => (ulong)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);

    [DllImport("libc", EntryPoint = "prctl")]
    private static extern int Prctl(int operation, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}
