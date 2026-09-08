using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Runtimes;

namespace RelaxKonOS.Server.Tunnels;

/// <summary>Host-local frps supervisor. Configuration is private, generated TOML is never returned over HTTP.</summary>
public sealed class ManagedFrpsService(IHostEnvironment environment, IDataProtectionProvider dataProtection, IRuntimeManager runtimes, IServiceScopeFactory scopes)
    : IManagedFrpsService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDataProtector _protector = dataProtection.CreateProtector("RelaxKonOS.Tunnels.ManagedFrps.v1");
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data", "runtimes", "frp", "frps");
    private readonly ConcurrentQueue<TunnelLogEntryDto> _logs = new();
    private Process? _process;
    private DateTimeOffset? _startedAt;
    private ManagedFrpsState _state = ManagedFrpsState.NotConfigured;
    private string _problemCode = "";

    public async Task<ManagedFrpsConfigurationDto> GetAsync(CancellationToken ct)
    {
        var saved = await ReadAsync(ct);
        return ToDto(saved);
    }

    public async Task<ManagedFrpsConfigurationDto> GetForEditingAsync(string actorUserId, CancellationToken ct)
    {
        var saved = await ReadAsync(ct);
        var value = ToDto(saved, includeToken: true);
        if (value.Token is not null) await AuditAsync(actorUserId, "frps.token.read", "succeeded", "", ct);
        return value;
    }

    public async Task<ManagedFrpsConfigurationDto> UpdateAsync(UpdateManagedFrpsConfigurationRequest request, string actorUserId, CancellationToken ct)
    {
        if (!request.Confirmed) throw new ManagedFrpsValidationException("tunnel.frps.confirmation_required");
        await _gate.WaitAsync(ct);
        try
        {
            var previous = await ReadAsync(ct);
            var validation = Validate(request, previous);
            if (validation is not null) throw new ManagedFrpsValidationException(validation);
            var value = new StoredConfiguration(request.BindAddress.Trim(), request.BindPort, request.AllowPorts?.ToArray() ?? [],
                request.VhostHttpPort, request.VhostHttpsPort, request.ForceTls,
                string.IsNullOrWhiteSpace(request.Token) ? previous?.ProtectedToken : _protector.Protect(request.Token.Trim()),
                request.DashboardEnabled, request.DashboardAddress.Trim(), request.DashboardPort, request.DashboardUser?.Trim(),
                string.IsNullOrWhiteSpace(request.DashboardPassword) ? previous?.ProtectedDashboardPassword : _protector.Protect(request.DashboardPassword.Trim()));
            await WriteAsync(value, ct);
            await AuditAsync(actorUserId, "frps.configure", "succeeded", "", ct);
            return ToDto(value, includeToken: true);
        }
        catch (ManagedFrpsValidationException ex) { await AuditAsync(actorUserId, "frps.configure", "failed", ex.ProblemCode, ct); throw; }
        finally { _gate.Release(); }
    }

    public async Task<TunnelOperationResultDto> StartAsync(string actorUserId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var config = await ReadAsync(ct);
            if (config is null) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_not_configured", ct);
            if (_process is { HasExited: false }) return new(true, TunnelConnectionState.Connected);
            var runtime = await runtimes.GetManagedFrpsStatusAsync(ct);
            if (runtime.State != TunnelRuntimeState.Available || string.IsNullOrWhiteSpace(runtime.ExecutablePath)) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.managed_runtime_not_installed", ct);
            if (string.IsNullOrEmpty(config.ProtectedToken)) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_token_required", ct);
            if (config.DashboardEnabled && (string.IsNullOrEmpty(config.DashboardUser) || string.IsNullOrEmpty(config.ProtectedDashboardPassword))) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_dashboard_credentials_required", ct);
            var ports = Ports(config).Distinct().ToArray();
            foreach (var port in ports) EnsurePortAvailable(config.BindAddress, port);
            Directory.CreateDirectory(_root); SetPrivateDirectory(_root);
            var toml = Path.Combine(_root, "frps.toml");
            var temporary = Path.Combine(_root, $".frps.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporary, GenerateToml(config), ct); SetPrivateFile(temporary);
            if (!await VerifyAsync(runtime.ExecutablePath, temporary, ct)) { File.Delete(temporary); return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_config_verify_failed", ct); }
            File.Move(temporary, toml, overwrite: true); SetPrivateFile(toml);
            _state = ManagedFrpsState.Starting; _problemCode = "";
            var process = new Process { StartInfo = new ProcessStartInfo(runtime.ExecutablePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }, EnableRaisingEvents = true };
            process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(toml);
            process.OutputDataReceived += (_, e) => AppendLog("information", e.Data);
            process.ErrorDataReceived += (_, e) => AppendLog("error", e.Data);
            process.Exited += (_, _) => { _state = ManagedFrpsState.Failed; _problemCode = "tunnel.frps_exited"; AppendLog("error", "frps exited."); };
            if (!process.Start()) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_start_failed", ct);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            _process = process; _startedAt = DateTimeOffset.UtcNow;
            await Task.Delay(250, ct);
            if (process.HasExited) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_start_failed", ct);
            _state = ManagedFrpsState.Running; AppendLog("information", "frps started.");
            return await CompleteAsync(actorUserId, "frps.start", true, "", ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<TunnelOperationResultDto> StopAsync(string actorUserId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is { } process)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(ct); } }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); _process = null; }
            }
            _state = (await ReadAsync(ct)) is null ? ManagedFrpsState.NotConfigured : ManagedFrpsState.Stopped;
            _startedAt = null; _problemCode = ""; AppendLog("information", "frps stopped.");
            return await CompleteAsync(actorUserId, "frps.stop", true, "", ct);
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TunnelLogEntryDto>>(_logs.ToArray());
    private async Task<TunnelOperationResultDto> CompleteAsync(string actor, string action, bool succeeded, string code, CancellationToken ct) { await AuditAsync(actor, action, succeeded ? "succeeded" : "failed", code, ct); return new(succeeded, succeeded ? TunnelConnectionState.Connected : TunnelConnectionState.RuntimeUnavailable, code); }
    private async Task AuditAsync(string actor, string action, string result, string code, CancellationToken ct) { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<ITunnelAudit>().RecordAsync(actor, action, null, result, code, ct); }
    private static string? Validate(UpdateManagedFrpsConfigurationRequest r, StoredConfiguration? previous)
    {
        if (!IPAddress.TryParse(r.BindAddress, out _) || !IsPort(r.BindPort)) return "tunnel.frps_invalid_bind";
        if (r.AllowPorts is not { Count: > 0 } || r.AllowPorts.Count > 64 || r.AllowPorts.Any(x => !IsPort(x.Start) || !IsPort(x.End) || x.Start > x.End)) return "tunnel.frps_invalid_allow_ports";
        if (r.VhostHttpPort is { } http && !IsPort(http) || r.VhostHttpsPort is { } https && !IsPort(https) || r.DashboardEnabled && (r.DashboardPort is not { } port || !IsPort(port) || !IPAddress.TryParse(r.DashboardAddress, out _))) return "tunnel.frps_invalid_port";
        var ports = new[] { r.BindPort, r.VhostHttpPort, r.VhostHttpsPort, r.DashboardEnabled ? r.DashboardPort : null }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        if (ports.Distinct().Count() != ports.Length) return "tunnel.frps_port_conflict";
        if (string.IsNullOrWhiteSpace(r.Token) && string.IsNullOrWhiteSpace(previous?.ProtectedToken)) return "tunnel.frps_token_required";
        if (r.DashboardEnabled && (string.IsNullOrWhiteSpace(r.DashboardUser) || (string.IsNullOrWhiteSpace(r.DashboardPassword) && string.IsNullOrWhiteSpace(previous?.ProtectedDashboardPassword)))) return "tunnel.frps_dashboard_credentials_required";
        return null;
    }
    private static IEnumerable<int> Ports(StoredConfiguration c) { yield return c.BindPort; if (c.VhostHttpPort is { } http) yield return http; if (c.VhostHttpsPort is { } https) yield return https; if (c.DashboardEnabled && c.DashboardPort is { } dashboard) yield return dashboard; }
    private static void EnsurePortAvailable(string address, int port) { var endpoint = new IPEndPoint(IPAddress.Parse(address), port); using var listener = new TcpListener(endpoint); try { listener.Start(); } catch (SocketException) { throw new ManagedFrpsValidationException("tunnel.frps_port_in_use"); } finally { listener.Stop(); } }
    private static bool IsPort(int value) => value is > 0 and <= 65535;
    private async Task<StoredConfiguration?> ReadAsync(CancellationToken ct) { var path = Path.Combine(_root, "config.json"); if (!File.Exists(path)) return null; await using var input = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<StoredConfiguration>(input, cancellationToken: ct); }
    private async Task WriteAsync(StoredConfiguration value, CancellationToken ct) { Directory.CreateDirectory(_root); SetPrivateDirectory(_root); var temporary = Path.Combine(_root, $".config.{Guid.NewGuid():N}.tmp"); await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, value, cancellationToken: ct); SetPrivateFile(temporary); File.Move(temporary, Path.Combine(_root, "config.json"), overwrite: true); SetPrivateFile(Path.Combine(_root, "config.json")); }
    private string GenerateToml(StoredConfiguration c)
    {
        var token = _protector.Unprotect(c.ProtectedToken!); var dashboardPassword = string.IsNullOrEmpty(c.ProtectedDashboardPassword) ? null : _protector.Unprotect(c.ProtectedDashboardPassword);
        var lines = new List<string> { $"bindAddr = \"{c.BindAddress}\"", $"bindPort = {c.BindPort}", $"auth.token = \"{Escape(token)}\"" };
        if (c.AllowPorts.Length > 0) lines.Add("allowPorts = [" + string.Join(", ", c.AllowPorts.Select(x => x.Start == x.End ? $"{{ single = {x.Start} }}" : $"{{ start = {x.Start}, end = {x.End} }}")) + "]");
        if (c.VhostHttpPort is { } http) lines.Add($"vhostHTTPPort = {http}"); if (c.VhostHttpsPort is { } https) lines.Add($"vhostHTTPSPort = {https}"); if (c.ForceTls) lines.Add("transport.tls.force = true");
        if (c.DashboardEnabled) { lines.Add($"webServer.addr = \"{c.DashboardAddress}\""); lines.Add($"webServer.port = {c.DashboardPort}"); lines.Add($"webServer.user = \"{Escape(c.DashboardUser!)}\""); lines.Add($"webServer.password = \"{Escape(dashboardPassword!)}\""); }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
    private static async Task<bool> VerifyAsync(string executable, string config, CancellationToken ct) { using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } }; process.StartInfo.ArgumentList.Add("verify"); process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(config); try { if (!process.Start()) return false; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10)); await Task.WhenAll(process.StandardOutput.ReadToEndAsync(timeout.Token), process.StandardError.ReadToEndAsync(timeout.Token), process.WaitForExitAsync(timeout.Token)); return process.ExitCode == 0; } catch { return false; } }
    private ManagedFrpsConfigurationDto ToDto(StoredConfiguration? c, bool includeToken = false)
    {
        // Runtime state is intentionally in-memory. After a RelaxKonOS Server restart a saved
        // configuration is not running, but it is still configured and must not be presented as new.
        var state = c is not null && _state == ManagedFrpsState.NotConfigured ? ManagedFrpsState.Stopped : _state;
        return c is null
            ? new("0.0.0.0", 7000, [], null, null, false, false, false, "127.0.0.1", null, null, false, state, _problemCode, _startedAt)
            : new(c.BindAddress, c.BindPort, c.AllowPorts, c.VhostHttpPort, c.VhostHttpsPort, c.ForceTls, !string.IsNullOrEmpty(c.ProtectedToken), c.DashboardEnabled, c.DashboardAddress, c.DashboardPort, c.DashboardUser, !string.IsNullOrEmpty(c.ProtectedDashboardPassword), state, _problemCode, _startedAt, includeToken && !string.IsNullOrEmpty(c.ProtectedToken) ? _protector.Unprotect(c.ProtectedToken) : null);
    }
    private void AppendLog(string level, string? message) { if (string.IsNullOrWhiteSpace(message)) return; message = Regex.Replace(message, "(?i)(token|secret|password)\\s*[:=]\\s*[^\\s,]+", "$1=<redacted>"); _logs.Enqueue(new(DateTimeOffset.UtcNow, level, message.Length > 1024 ? message[..1024] : message)); while (_logs.Count > 200) _logs.TryDequeue(out _); }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static void SetPrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void SetPrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    public void Dispose() { _gate.Dispose(); _process?.Dispose(); }
    private sealed record StoredConfiguration(string BindAddress, int BindPort, TunnelPortRangeDto[] AllowPorts, int? VhostHttpPort, int? VhostHttpsPort, bool ForceTls, string? ProtectedToken, bool DashboardEnabled, string DashboardAddress, int? DashboardPort, string? DashboardUser, string? ProtectedDashboardPassword);
}

public interface IManagedFrpsService
{
    Task<ManagedFrpsConfigurationDto> GetAsync(CancellationToken cancellationToken);
    Task<ManagedFrpsConfigurationDto> GetForEditingAsync(string actorUserId, CancellationToken cancellationToken);
    Task<ManagedFrpsConfigurationDto> UpdateAsync(UpdateManagedFrpsConfigurationRequest request, string actorUserId, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> StartAsync(string actorUserId, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> StopAsync(string actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(CancellationToken cancellationToken);
}
public sealed class ManagedFrpsValidationException(string problemCode) : Exception(problemCode) { public string ProblemCode { get; } = problemCode; }
