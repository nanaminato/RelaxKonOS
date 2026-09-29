using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Guardian.Agent;

/// <summary>Agent-owned one-shot jobs. A phone and an HTTP request have no process lifetime authority.</summary>
internal sealed class ScriptTaskSupervisor(GuardianAgentOptions options)
{
    private readonly ConcurrentDictionary<string, ScriptRun> _tasks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _persistence = new(1, 1);
    private string TasksPath => Path.Combine(options.DataDirectory, "scripts.json");

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataDirectory);
        ProtectDataDirectory();
        if (!File.Exists(TasksPath)) return;
        var snapshots = JsonSerializer.Deserialize<ScriptTaskDto[]>(await File.ReadAllTextAsync(TasksPath, cancellationToken), RelaxKonOSJsonOptions.Default) ?? [];
        foreach (var snapshot in snapshots)
        {
            var recovered = snapshot.State is "queued" or "running" or "cancelling"
                ? snapshot with { State = "interrupted", FinishedAt = DateTimeOffset.UtcNow, ProblemCode = "guardian.script_agent_restarted" }
                : snapshot;
            _tasks[recovered.Id] = new ScriptRun(recovered);
        }
        PruneCompleted();
        await PersistAsync(CancellationToken.None);
    }

    public async Task<GuardianAgentResponse> HandleAsync(GuardianAgentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OwnerIdentity)) return new(false, "guardian.script_owner_required");
        return request.Command switch
        {
            "script-list" => new(true, string.Empty, Scripts: _tasks.Values.Select(task => task.Snapshot())
                .Where(task => task.OwnerIdentity == request.OwnerIdentity).OrderByDescending(task => task.CreatedAt)
                .Take(100).Select(task => task with { Output = [] }).ToArray()),
            "script-get" => Get(request.OwnerIdentity, request.WorkloadId),
            "script-cancel" => await CancelAsync(request.OwnerIdentity, request.WorkloadId),
            "script-submit" => await SubmitAsync(request, cancellationToken),
            _ => new(false, "guardian.ipc_invalid_command"),
        };
    }

    private GuardianAgentResponse Get(string owner, string? id) =>
        id is not null && _tasks.TryGetValue(id, out var task) && task.Snapshot().OwnerIdentity == owner
            ? new(true, string.Empty, ScriptTask: task.Snapshot())
            : new(false, "guardian.script_not_found");

    private async Task<GuardianAgentResponse> SubmitAsync(GuardianAgentRequest request, CancellationToken cancellationToken)
    {
        var definition = request.Script;
        string? problem = null;
        if (definition is null || definition.OwnerIdentity != request.OwnerIdentity ||
            !Guid.TryParse(definition.Id, out _) || !Valid(definition, out problem))
            return new(false, problem ?? "guardian.script_invalid");
        if (_tasks.TryGetValue(definition.Id, out var previous))
            return previous.Snapshot().OwnerIdentity == request.OwnerIdentity
                ? new(true, string.Empty, ScriptTask: previous.Snapshot())
                : new(false, "guardian.script_conflict");
        var start = WorkloadSupervisor.CreateStartInfo(new ProcessDefinitionDto(
            definition.Id, definition.Id, definition.ExecutablePath, definition.Arguments,
            definition.WorkingDirectory, RunAs: definition.RunAs, RunAsIdentity: definition.RunAsIdentity),
            out problem, definition.Environment);
        if (start is null) return new(false, problem ?? "guardian.script_launch_failed");
        var snapshot = new ScriptTaskDto(definition.Id, definition.OwnerIdentity, definition.ExecutablePath,
            definition.RunAs, "queued", DateTimeOffset.UtcNow, null, null, null, [], false);
        var run = new ScriptRun(snapshot);
        if (!_tasks.TryAdd(definition.Id, run)) return new(false, "guardian.script_conflict");
        PruneCompleted();
        await PersistAsync(cancellationToken);
        run.Execution = Task.Run(() => ExecuteAsync(run, start, definition.TimeoutSeconds));
        return new(true, string.Empty, ScriptTask: run.Snapshot());
    }

    private bool Valid(ScriptTaskDefinitionDto value, out string? problem)
    {
        problem = "guardian.script_invalid";
        if (string.IsNullOrWhiteSpace(value.OwnerIdentity) || string.IsNullOrWhiteSpace(value.RunAs) ||
            value.RunAs.Contains('\0') || value.RunAs.StartsWith('-') ||
            options.UserMode && !string.Equals(value.RunAs, Environment.UserName, StringComparison.Ordinal)) return false;
        if (value.Arguments is null || value.Environment is null) return false;
        if (!WorkloadSupervisor.ValidateStableRunAsIdentity(new ProcessDefinitionDto(value.Id, value.Id,
            value.ExecutablePath, value.Arguments, value.WorkingDirectory,
            RunAs: value.RunAs, RunAsIdentity: value.RunAsIdentity), out problem)) return false;
        problem = "guardian.script_invalid";
        if (string.IsNullOrWhiteSpace(value.ExecutablePath) || !Path.IsPathFullyQualified(value.ExecutablePath) || !File.Exists(value.ExecutablePath) ||
            string.IsNullOrWhiteSpace(value.WorkingDirectory) || !Path.IsPathFullyQualified(value.WorkingDirectory) || !Directory.Exists(value.WorkingDirectory) ||
            value.TimeoutSeconds is < 1 or > 3600 || value.Arguments.Count > 64 || value.Environment.Count > 32) return false;
        if (value.Arguments.Any(arg => arg.Length > 4096 || arg.Contains('\0')) ||
            value.Environment.Any(entry => entry.Key.Length is < 1 or > 128 || entry.Key[0] is < 'A' or > 'z' ||
                entry.Key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_') || entry.Value.Length > 4096 || entry.Value.Contains('\0'))) return false;
        problem = null;
        return true;
    }

    private async Task ExecuteAsync(ScriptRun run, ProcessStartInfo start, int timeoutSeconds)
    {
        try
        {
            if (run.Cancel.IsCancellationRequested)
            {
                run.Update(snapshot => snapshot with { State = "cancelled", FinishedAt = DateTimeOffset.UtcNow });
                return;
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            run.Process = process;
            run.Update(snapshot => snapshot with { State = run.Cancel.IsCancellationRequested ? "cancelling" : "running" });
            await PersistAsync(CancellationToken.None);
            var stdout = CaptureAsync(process.StandardOutput, run, "stdout");
            var stderr = CaptureAsync(process.StandardError, run, "stderr");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, run.Cancel.Token);
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(stdout, stderr);
            var state = run.Cancel.IsCancellationRequested ? "cancelled" : timeout.IsCancellationRequested ? "timedOut" :
                process.ExitCode == 0 ? "succeeded" : "failed";
            run.Update(snapshot => snapshot with { State = state, FinishedAt = DateTimeOffset.UtcNow,
                ExitCode = process.ExitCode, ProblemCode = state == "timedOut" ? "guardian.script_timeout" : null });
        }
        catch
        {
            run.Update(snapshot => snapshot with { State = "failed", FinishedAt = DateTimeOffset.UtcNow,
                ProblemCode = "guardian.script_launch_failed" });
        }
        finally { run.Process = null; await PersistAsync(CancellationToken.None); }
    }

    private static async Task CaptureAsync(StreamReader reader, ScriptRun run, string stream)
    {
        var buffer = new char[512];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer);
                if (count == 0) break;
                // Bounded chunks also handle tools that never write a newline.
                var text = new string(buffer, 0, count).Select(ch => char.IsControl(ch) && ch is not '\n' and not '\t' ? ' ' : ch).ToArray();
                run.Append(new ScriptOutputLineDto(DateTimeOffset.UtcNow, stream, new string(text)));
            }
        }
        catch (IOException) { }
    }

    private async Task<GuardianAgentResponse> CancelAsync(string owner, string? id)
    {
        if (id is null || !_tasks.TryGetValue(id, out var run) || run.Snapshot().OwnerIdentity != owner)
            return new(false, "guardian.script_not_found");
        if (run.Snapshot().State is "queued" or "running")
        {
            run.Update(snapshot => snapshot with { State = "cancelling" });
            run.Cancel.Cancel();
        }
        await PersistAsync(CancellationToken.None);
        return new(true, string.Empty, ScriptTask: run.Snapshot());
    }

    public async Task StopAllAsync()
    {
        var running = _tasks.Values.Where(run => run.Snapshot().State is "queued" or "running" or "cancelling").ToArray();
        foreach (var run in running) run.Cancel.Cancel();
        await Task.WhenAll(running.Select(run => run.Execution ?? Task.CompletedTask));
        await PersistAsync(CancellationToken.None);
    }

    private void PruneCompleted()
    {
        var excess = _tasks.Count - 200;
        if (excess <= 0) return;
        foreach (var task in _tasks.Values.Select(run => run.Snapshot())
            .Where(task => task.State is not ("queued" or "running" or "cancelling"))
            .OrderBy(task => task.CreatedAt).Take(excess))
            _tasks.TryRemove(task.Id, out _);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _persistence.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            ProtectDataDirectory();
            var file = TasksPath + ".tmp";
            var snapshots = _tasks.Values.Select(run => run.Snapshot()).OrderByDescending(task => task.CreatedAt).ToArray();
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(snapshots, RelaxKonOSJsonOptions.Default), cancellationToken);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(file, TasksPath, overwrite: true);
        }
        finally { _persistence.Release(); }
    }

    private void ProtectDataDirectory()
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(options.DataDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class ScriptRun(ScriptTaskDto initial)
    {
        private readonly object _gate = new();
        private ScriptTaskDto _snapshot = initial;
        public Process? Process { get; set; }
        public Task? Execution { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
        public ScriptTaskDto Snapshot() { lock (_gate) return _snapshot; }
        public void Update(Func<ScriptTaskDto, ScriptTaskDto> update) { lock (_gate) _snapshot = update(_snapshot); }
        public void Append(ScriptOutputLineDto line) => Update(snapshot =>
        {
            var lines = snapshot.Output.ToList();
            lines.Add(line);
            var truncated = snapshot.OutputTruncated;
            if (lines.Count > 120) { lines.RemoveAt(0); truncated = true; }
            return snapshot with { Output = lines, OutputTruncated = truncated };
        });
    }
}
