using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Runtimes;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Tunnels;

/// <summary>Applies validated desired state to isolated frpc child processes. It never downloads FRP or forwards traffic.</summary>
public sealed class FrpTunnelProvider(IServiceScopeFactory scopes, IHostEnvironment environment, IRuntimeManager runtimes) : ITunnelProvider
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _profileLocks = new();
    private readonly ConcurrentDictionary<Guid, ManagedProcess> _processes = new();
    private readonly ConcurrentDictionary<Guid, RuntimeSnapshot> _states = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<TunnelLogEntryDto>> _logs = new();
    private readonly string _configurationRoot = Path.Combine(environment.ContentRootPath, "data", "tunnels", "frp");
    public string ProviderId => "frp";

    public Task<TunnelRuntimeDto> GetStatusAsync(CancellationToken ct) => runtimes.GetManagedFrpcStatusAsync(ct);
    public async Task<IReadOnlyList<TunnelDefinitionDto>> ListAsync(string userId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>();
        return (await db.TunnelDefinitions.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Name).ToListAsync(ct)).Select(x => ToDto(x) with { State = _states.TryGetValue(x.ServerProfileId, out var state) ? state.State : TunnelConnectionState.SavedNotApplied, ProblemCode = _states.TryGetValue(x.ServerProfileId, out state) ? state.ProblemCode : "" }).ToList();
    }

    public async Task<TunnelOperationResultDto> ApplyAsync(Guid profileId, string userId, CancellationToken ct)
    {
        var gate = _profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var profile = await db.TunnelServerProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profileId && x.UserId == userId, ct);
            if (profile is null) return new(false, TunnelConnectionState.Unknown, "tunnel.profile_not_found");
            var definitions = await db.TunnelDefinitions.AsNoTracking().Where(x => x.ServerProfileId == profileId && x.UserId == userId).ToListAsync(ct);
            var dto = new TunnelServerProfileDto(profile.Id, profile.Name, profile.Host, profile.Port, profile.AuthKind, await secrets.HasProfileTokenAsync(profile.Id, ct), profile.TlsMode, profile.RuntimeMode, profile.ExternalExecutablePath, profile.Revision, profile.CreatedAt, profile.UpdatedAt);
            var token = profile.AuthKind == TunnelAuthKind.Token ? await secrets.GetProfileTokenAsync(profile.Id, ct) : null;
            var executable = await ResolveExecutableAsync(profile, ct);
            if (executable is null) return await CompleteAsync(db, profileId, userId, new(false, TunnelConnectionState.RuntimeUnavailable, profile.RuntimeMode == TunnelRuntimeMode.Managed ? "tunnel.managed_runtime_not_installed" : "tunnel.external_invalid"), ct);
            var folder = Path.Combine(_configurationRoot, profile.Id.ToString("N")); Directory.CreateDirectory(folder); SetPrivateDirectory(folder);
            var configuration = Path.Combine(folder, "frpc.toml"); var temporary = Path.Combine(folder, $".frpc.{Guid.NewGuid():N}.tmp"); var backup = Path.Combine(folder, ".frpc.previous.toml");
            await File.WriteAllTextAsync(temporary, FrpTomlGenerator.Generate(dto, definitions.Select(ToDto), token), ct); SetPrivateFile(temporary);
            if (!await VerifyAsync(executable, temporary, ct)) { File.Delete(temporary); return await CompleteAsync(db, profileId, userId, new(false, TunnelConnectionState.SavedNotApplied, "tunnel.config_verify_failed"), ct); }
            try
            {
                if (File.Exists(configuration)) { File.Copy(configuration, backup, overwrite: true); SetPrivateFile(backup); }
                File.Move(temporary, configuration, overwrite: true);
                SetPrivateFile(configuration);
                await StopCoreAsync(profileId);
                // Mark the previous process as no longer connected before starting its
                // replacement.  The new process can report a successful login before
                // the 200 ms startup check below completes.
                _states[profileId] = new(TunnelConnectionState.Starting, "");
                var started = Start(profileId, executable, configuration, profile.RuntimeMode == TunnelRuntimeMode.Managed);
                _processes[profileId] = started;
                await Task.Delay(200, ct);
                if (started.Process.HasExited) throw new InvalidOperationException();
                return await CompleteAsync(db, profileId, userId, new(true, TunnelConnectionState.Starting), ct);
            }
            catch
            {
                await StopCoreAsync(profileId);
                if (File.Exists(backup))
                {
                    File.Move(backup, configuration, overwrite: true);
                    try
                    {
                        var restored = Start(profileId, executable, configuration, profile.RuntimeMode == TunnelRuntimeMode.Managed);
                        _processes[profileId] = restored;
                        await Task.Delay(200, ct);
                        if (!restored.Process.HasExited) AppendLog(profileId, "information", "Previous verified configuration was restored after apply failure.");
                    }
                    catch { AppendLog(profileId, "error", "Previous configuration could not be restarted after apply failure."); }
                }
                return await CompleteAsync(db, profileId, userId, new(false, TunnelConnectionState.SavedNotApplied, "tunnel.runtime_start_failed"), ct);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (SecretValidationException ex) { _states[profileId] = new(TunnelConnectionState.SavedNotApplied, ex.ProblemCode); return new(false, TunnelConnectionState.SavedNotApplied, ex.ProblemCode); }
        finally { gate.Release(); }
    }

    public async Task<TunnelOperationResultDto> StopAsync(Guid profileId, string userId, CancellationToken ct)
    {
        var gate = _profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>();
            if (!await db.TunnelServerProfiles.AsNoTracking().AnyAsync(x => x.Id == profileId && x.UserId == userId, ct))
                return new(false, TunnelConnectionState.Unknown, "tunnel.profile_not_found");
            await StopCoreAsync(profileId); return await CompleteAsync(db, profileId, userId, new(true, TunnelConnectionState.Disconnected), ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>Stops all host-local child processes before their managed runtime is removed.</summary>
    public async Task StopManagedProcessesAsync(CancellationToken ct)
    {
        foreach (var profileId in _processes.Where(x => x.Value.IsManaged).Select(x => x.Key).ToArray())
        {
            var gate = _profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                await StopCoreAsync(profileId);
                _states[profileId] = new(TunnelConnectionState.Disconnected, "");
            }
            finally { gate.Release(); }
        }
    }

    public async Task<IReadOnlyList<TunnelLogEntryDto>?> GetLogsAsync(Guid profileId, string userId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>();
        if (!await db.TunnelServerProfiles.AsNoTracking().AnyAsync(x => x.Id == profileId && x.UserId == userId, ct)) return null;
        return _logs.TryGetValue(profileId, out var records) ? records.ToArray() : [];
    }

    private async Task<string?> ResolveExecutableAsync(RelaxKonOS.Server.Domain.TunnelServerProfile profile, CancellationToken ct)
    {
        if (profile.RuntimeMode == TunnelRuntimeMode.Managed)
        {
            var managedStatus = await runtimes.GetManagedFrpcStatusAsync(ct); return managedStatus.State == TunnelRuntimeState.Available ? managedStatus.ExecutablePath : null;
        }
        var externalStatus = await runtimes.DetectExternalFrpcAsync(profile.ExternalExecutablePath!, ct); return externalStatus.State == TunnelRuntimeState.Available ? externalStatus.ExecutablePath : null;
    }
    private static async Task<bool> VerifyAsync(string executable, string config, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("verify"); process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(config);
        try
        {
            process.Start(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            return process.ExitCode == 0;
        }
        catch { return false; }
    }
    private ManagedProcess Start(Guid profileId, string executable, string config, bool isManaged)
    {
        var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true }, EnableRaisingEvents = true };
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(config);
        process.OutputDataReceived += (_, eventArgs) => AppendLog(profileId, "information", eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AppendLog(profileId, "error", eventArgs.Data);
        process.Exited += (_, _) => { if (_processes.ContainsKey(profileId)) _states[profileId] = new(TunnelConnectionState.Disconnected, "tunnel.runtime_exited"); };
        if (!process.Start()) throw new InvalidOperationException();
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return new ManagedProcess(process, process.Id, process.StartTime.ToUniversalTime(), isManaged);
    }
    private async Task StopCoreAsync(Guid profileId)
    {
        if (!_processes.TryRemove(profileId, out var managed)) return;
        try
        {
            if (!managed.Process.HasExited && managed.Process.Id == managed.ProcessId && managed.Process.StartTime.ToUniversalTime() == managed.StartedAt)
            {
                managed.Process.Kill(entireProcessTree: true); await managed.Process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { }
        finally { managed.Process.Dispose(); }
    }
    private static TunnelDefinitionDto ToDto(RelaxKonOS.Server.Domain.TunnelDefinition x) => new(x.Id, x.ServerProfileId, x.Name, x.ProviderId, x.Protocol, x.LocalHost, x.LocalPort, x.RemotePort, x.Domain, x.Enabled, x.Encryption, x.Compression, x.Revision, x.CreatedAt, x.UpdatedAt);
    private async Task<TunnelOperationResultDto> CompleteAsync(RelaxKonOSDbContext db, Guid profileId, string userId, TunnelOperationResultDto result, CancellationToken ct)
    {
        var snapshot = new RuntimeSnapshot(result.State, result.ProblemCode);
        _states.AddOrUpdate(
            profileId,
            snapshot,
            (_, current) => result.State == TunnelConnectionState.Starting && current.State == TunnelConnectionState.Connected
                ? current
                : snapshot);
        using var auditScope = scopes.CreateScope();
        await auditScope.ServiceProvider.GetRequiredService<ITunnelAudit>().RecordAsync(userId, result.Succeeded ? "profile.apply" : "profile.apply_failed", profileId, result.Succeeded ? "succeeded" : "failed", result.ProblemCode, ct);
        return result;
    }
    private void AppendLog(Guid profileId, string level, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (raw.Contains("login to server success", StringComparison.OrdinalIgnoreCase))
            _states[profileId] = new(TunnelConnectionState.Connected, "");
        else if (raw.Contains("login to server failed", StringComparison.OrdinalIgnoreCase)
                 || raw.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
            _states[profileId] = new(TunnelConnectionState.Disconnected, "tunnel.authentication_failed");
        var message = Regex.Replace(raw, "(?i)(token|secret|password)\\s*[:=]\\s*[^\\s,]+", "$1=<redacted>");
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim(); if (message.Length > 1024) message = message[..1024];
        var queue = _logs.GetOrAdd(profileId, _ => new ConcurrentQueue<TunnelLogEntryDto>()); queue.Enqueue(new(DateTimeOffset.UtcNow, level, message)); while (queue.Count > 200) queue.TryDequeue(out _);
    }
    private static void SetPrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void SetPrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    private sealed record ManagedProcess(Process Process, int ProcessId, DateTime StartedAt, bool IsManaged);
    private sealed record RuntimeSnapshot(TunnelConnectionState State, string ProblemCode);
}
