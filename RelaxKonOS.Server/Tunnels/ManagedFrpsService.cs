using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Runtimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Tunnels;

/// <summary>Host-local frps supervisor. Configuration is private, generated TOML is never returned over HTTP.</summary>
public sealed class ManagedFrpsService(IHostEnvironment environment, IDataProtectionProvider dataProtection, IRuntimeManager runtimes, IServiceScopeFactory scopes, ManagedRuntimeOperations windowsRuntime)
    : IManagedFrpsService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDataProtector _protector = dataProtection.CreateProtector("RelaxKonOS.Tunnels.ManagedFrps.v1");
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data", "runtimes", "frp", "frps");
    private DateTimeOffset? _startedAt;
    private ManagedFrpsState _state = ManagedFrpsState.NotConfigured;
    private string _problemCode = "";
    private long? _appliedRevision;

    public async Task<ManagedFrpsConfigurationDto> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var saved = await ReadAsync(ct);
            if (saved is not null)
            {
                var result = await windowsRuntime.ExecuteAsync(new(ManagedRuntime.Frps, ManagedRuntimeAction.Status), ct);
                _state = result.Success ? result.ComponentProcess?.Running == true ? ManagedFrpsState.Running : ManagedFrpsState.Stopped : ManagedFrpsState.Failed;
                _problemCode = result.Success ? "" : ManagedRuntimeOperations.Problem(result);
                _startedAt = result.ComponentProcess?.StartedAt;
                _appliedRevision = long.TryParse(result.ComponentProcess?.AppliedIdentity, out var applied) ? applied : null;
            }
            return ToDto(saved);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagedFrpsConfigurationDto> GetForEditingAsync(string actorUserId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var saved = await ReadAsync(ct);
            var value = ToDto(saved);
            if (value.Token is not null) await AuditAsync(actorUserId, "frps.token.read", "succeeded", "", ct);
            return value;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagedFrpsConfigurationDto> UpdateAsync(UpdateManagedFrpsConfigurationRequest request, string actorUserId, CancellationToken ct)
    {
        if (!request.Confirmed) throw new ManagedFrpsValidationException("tunnel.frps.confirmation_required");
        await _gate.WaitAsync(ct);
        try
        {
            var previous = await ReadAsync(ct);
            if (request.ExpectedRevision != (previous?.Revision ?? 0)) throw new ManagedFrpsRevisionConflictException();
            var validation = Validate(request, previous);
            if (validation is not null) throw new ManagedFrpsValidationException(validation);
            var value = new StoredConfiguration(request.BindAddress.Trim(), request.BindPort, request.AllowPorts?.ToArray() ?? [],
                request.VhostHttpPort, request.VhostHttpsPort, request.ForceTls,
                string.IsNullOrWhiteSpace(request.Token) ? previous?.ProtectedToken : _protector.Protect(request.Token.Trim()),
                request.DashboardEnabled, request.DashboardAddress.Trim(), request.DashboardPort, request.DashboardUser?.Trim(),
                string.IsNullOrWhiteSpace(request.DashboardPassword) ? previous?.ProtectedDashboardPassword : _protector.Protect(request.DashboardPassword.Trim()), checked((previous?.Revision ?? 0) + 1));
            await WriteAsync(value, ct);
            if (previous is null) { _state = ManagedFrpsState.Stopped; }
            await AuditAsync(actorUserId, "frps.configure", "succeeded", "", ct);
            return ToDto(value);
        }
        catch (ManagedFrpsValidationException ex) { await AuditAsync(actorUserId, "frps.configure", "failed", ex.ProblemCode, ct); throw; }
        catch (ManagedFrpsRevisionConflictException) { await AuditAsync(actorUserId, "frps.configure", "failed", "tunnel.revision_conflict", ct); throw; }
        finally { _gate.Release(); }
    }

    public async Task<TunnelOperationResultDto> StartAsync(string actorUserId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var config = await ReadAsync(ct);
            if (config is null) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_not_configured", ct);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                var observed = await windowsRuntime.ExecuteAsync(new(ManagedRuntime.Frps, ManagedRuntimeAction.Status), ct);
                if (observed.Success && observed.ComponentProcess?.Running == true)
                {
                    _state = ManagedFrpsState.Running; _startedAt = observed.ComponentProcess.StartedAt;
                    _appliedRevision = long.TryParse(observed.ComponentProcess.AppliedIdentity, out var proof) ? proof : null;
                    return _appliedRevision == config.Revision ? new(true, TunnelConnectionState.Connected)
                        : await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_restart_required", ct);
                }
            }
            var runtime = await runtimes.GetManagedFrpsStatusAsync(ct);
            if (runtime.State != TunnelRuntimeState.Available || string.IsNullOrWhiteSpace(runtime.ExecutablePath)) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.managed_runtime_not_installed", ct);
            if (string.IsNullOrEmpty(config.ProtectedToken)) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_token_required", ct);
            if (config.DashboardEnabled && (string.IsNullOrEmpty(config.DashboardUser) || string.IsNullOrEmpty(config.ProtectedDashboardPassword))) return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_dashboard_credentials_required", ct);
            foreach (var endpoint in Endpoints(config)) EnsurePortAvailable(endpoint.Address, endpoint.Port);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                var request = new FrpsServiceConfiguration(config.BindAddress, config.BindPort, config.AllowPorts,
                    config.VhostHttpPort, config.VhostHttpsPort, config.ForceTls, _protector.Unprotect(config.ProtectedToken!),
                    config.DashboardEnabled, config.DashboardAddress, config.DashboardPort, config.DashboardUser,
                    config.ProtectedDashboardPassword is null ? null : _protector.Unprotect(config.ProtectedDashboardPassword));
                var result = await windowsRuntime.ExecuteAsync(new(ManagedRuntime.Frps, ManagedRuntimeAction.Start, runtime.Version, Server: request, AppliedIdentity: config.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct);
                _state = result.Success ? ManagedFrpsState.Running : ManagedFrpsState.Failed;
                if (result.Success) _appliedRevision = config.Revision;
                _startedAt = result.ComponentProcess?.StartedAt;
                _problemCode = result.Success ? "" : ManagedRuntimeOperations.Problem(result);
                return await CompleteAsync(actorUserId, "frps.start", result.Success, _problemCode, ct);
            }
            return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.runtime_helper_policy_denied", ct);
        }
        catch (ManagedFrpsValidationException ex) { return await CompleteAsync(actorUserId, "frps.start", false, ex.ProblemCode, ct); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return await CompleteAsync(actorUserId, "frps.start", false, "tunnel.frps_start_failed", ct); }
        finally { _gate.Release(); }
    }

    public async Task<TunnelOperationResultDto> StopAsync(string actorUserId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                var stopped = await windowsRuntime.ExecuteAsync(new(ManagedRuntime.Frps, ManagedRuntimeAction.Stop), ct);
                if (!stopped.Success) return await CompleteAsync(actorUserId, "frps.stop", false, ManagedRuntimeOperations.Problem(stopped), ct);
            }
            _state = (await ReadAsync(ct)) is null ? ManagedFrpsState.NotConfigured : ManagedFrpsState.Stopped;
            _startedAt = null; _appliedRevision = null; _problemCode = "";
            return await CompleteAsync(actorUserId, "frps.stop", true, "", ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(CancellationToken ct)
    {
        var result = await windowsRuntime.ExecuteAsync(new(ManagedRuntime.Frps, ManagedRuntimeAction.Status), ct);
        return result.Success ? result.ComponentProcess?.Logs ?? [] : [new(DateTimeOffset.UtcNow, "error", ManagedRuntimeOperations.Problem(result))];
    }
    private async Task<TunnelOperationResultDto> CompleteAsync(string actor, string action, bool succeeded, string code, CancellationToken ct)
    {
        _problemCode = code;
        if (!succeeded && code != "tunnel.frps_restart_required")
        {
            _state = code == "tunnel.frps_not_configured" ? ManagedFrpsState.NotConfigured
                : code == "tunnel.managed_runtime_not_installed" ? ManagedFrpsState.RuntimeUnavailable : ManagedFrpsState.Failed;
        }
        await AuditAsync(actor, action, succeeded ? "succeeded" : "failed", code, ct);
        var state = succeeded ? action == "frps.stop" ? TunnelConnectionState.Disconnected : TunnelConnectionState.Connected
            : code == "tunnel.frps_restart_required" ? TunnelConnectionState.SavedNotApplied
            : _state == ManagedFrpsState.Unknown ? TunnelConnectionState.Unknown : _state == ManagedFrpsState.RuntimeUnavailable ? TunnelConnectionState.RuntimeUnavailable : TunnelConnectionState.Disconnected;
        return new(succeeded, state, code);
    }
    private async Task AuditAsync(string actor, string action, string result, string code, CancellationToken ct) { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<ITunnelAudit>().RecordAsync(actor, action, null, result, code, ct); }
    private static string? Validate(UpdateManagedFrpsConfigurationRequest r, StoredConfiguration? previous)
    {
        if (!IPAddress.TryParse(r.BindAddress.Trim(), out _) || !IsPort(r.BindPort)) return "tunnel.frps_invalid_bind";
        if (r.AllowPorts is not { Count: > 0 } || r.AllowPorts.Count > 64 || r.AllowPorts.Any(x => !IsPort(x.Start) || !IsPort(x.End) || x.Start > x.End)) return "tunnel.frps_invalid_allow_ports";
        if (r.VhostHttpPort is { } http && !IsPort(http) || r.VhostHttpsPort is { } https && !IsPort(https) || r.DashboardEnabled && (r.DashboardPort is not { } port || !IsPort(port) || !IPAddress.TryParse(r.DashboardAddress, out _))) return "tunnel.frps_invalid_port";
        if (r.Token is not null && !ValidSecret(r.Token) || r.DashboardPassword is not null && !ValidSecret(r.DashboardPassword)) return "tunnel.frps_secret_invalid";
        if (r.DashboardUser is not null && (r.DashboardUser.Length > 128 || r.DashboardUser.Any(char.IsControl))) return "tunnel.frps_dashboard_credentials_required";
        var ports = new[] { r.BindPort, r.VhostHttpPort, r.VhostHttpsPort, r.DashboardEnabled ? r.DashboardPort : null }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        if (ports.Distinct().Count() != ports.Length) return "tunnel.frps_port_conflict";
        if (string.IsNullOrWhiteSpace(r.Token) && string.IsNullOrWhiteSpace(previous?.ProtectedToken)) return "tunnel.frps_token_required";
        if (r.DashboardEnabled && (string.IsNullOrWhiteSpace(r.DashboardUser) || (string.IsNullOrWhiteSpace(r.DashboardPassword) && string.IsNullOrWhiteSpace(previous?.ProtectedDashboardPassword)))) return "tunnel.frps_dashboard_credentials_required";
        return null;
    }
    private static bool ValidSecret(string value) => value.Length <= 4096 && !value.Any(char.IsControl);
    private static IEnumerable<(string Address, int Port)> Endpoints(StoredConfiguration c) { yield return (c.BindAddress, c.BindPort); if (c.VhostHttpPort is { } http) yield return (c.BindAddress, http); if (c.VhostHttpsPort is { } https) yield return (c.BindAddress, https); if (c.DashboardEnabled && c.DashboardPort is { } dashboard) yield return (c.DashboardAddress, dashboard); }
    private static void EnsurePortAvailable(string address, int port) { var endpoint = new IPEndPoint(IPAddress.Parse(address), port); using var listener = new TcpListener(endpoint); try { listener.Start(); } catch (SocketException) { throw new ManagedFrpsValidationException("tunnel.frps_port_in_use"); } finally { listener.Stop(); } }
    private static bool IsPort(int value) => value is > 0 and <= 65535;
    private async Task<StoredConfiguration?> ReadAsync(CancellationToken ct) { var path = Path.Combine(_root, "config.json"); if (!File.Exists(path)) return null; await using var input = File.OpenRead(path); var value = await JsonSerializer.DeserializeAsync<StoredConfiguration>(input, cancellationToken: ct); if (value is null || value.Revision <= 0) throw new InvalidDataException("Invalid frps configuration record."); return value; }
    private async Task WriteAsync(StoredConfiguration value, CancellationToken ct) { Directory.CreateDirectory(_root); SetPrivateDirectory(_root); var temporary = Path.Combine(_root, $".config.{Guid.NewGuid():N}.tmp"); await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, value, cancellationToken: ct); SetPrivateFile(temporary); File.Move(temporary, Path.Combine(_root, "config.json"), overwrite: true); SetPrivateFile(Path.Combine(_root, "config.json")); }
    private ManagedFrpsConfigurationDto ToDto(StoredConfiguration? c)
    {
        // Runtime state and applied proof come from the independent OS service, never a cached PID.
        var state = c is not null && _state == ManagedFrpsState.NotConfigured ? ManagedFrpsState.Unknown : _state;
        return c is null
            ? new("0.0.0.0", 7000, [], null, null, false, false, false, "127.0.0.1", null, null, false, state, 0, null, _problemCode, _startedAt)
            : new(c.BindAddress, c.BindPort, c.AllowPorts, c.VhostHttpPort, c.VhostHttpsPort, c.ForceTls, !string.IsNullOrEmpty(c.ProtectedToken), c.DashboardEnabled, c.DashboardAddress, c.DashboardPort, c.DashboardUser, !string.IsNullOrEmpty(c.ProtectedDashboardPassword), state, c.Revision, state is ManagedFrpsState.Running or ManagedFrpsState.Starting ? _appliedRevision : null, _problemCode, _startedAt, !string.IsNullOrEmpty(c.ProtectedToken) ? _protector.Unprotect(c.ProtectedToken) : null, !string.IsNullOrEmpty(c.ProtectedDashboardPassword) ? _protector.Unprotect(c.ProtectedDashboardPassword) : null);
    }
    private static void SetPrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void SetPrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    public void Dispose() { _gate.Dispose(); }
    private sealed record StoredConfiguration(string BindAddress, int BindPort, TunnelPortRangeDto[] AllowPorts, int? VhostHttpPort, int? VhostHttpsPort, bool ForceTls, string? ProtectedToken, bool DashboardEnabled, string DashboardAddress, int? DashboardPort, string? DashboardUser, string? ProtectedDashboardPassword, [property: System.Text.Json.Serialization.JsonRequired] long Revision);
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

public sealed class ManagedFrpsRevisionConflictException() : Exception("tunnel.revision_conflict");
