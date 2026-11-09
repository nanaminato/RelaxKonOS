using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.Platform.Processes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Retains the original worker and dispatch fence until native exit and both pipes are confirmed.</summary>
internal sealed class MihomoExternalVersionProbe(IProxyPlatformPaths paths, IServerModeResolver mode, IPrivilegedOperationTransport helper) : IAsyncDisposable
{
    private sealed record Dispatch(Guid OperationId, int? WorkerPid = null, DateTimeOffset? WorkerStartedAt = null,
        MihomoProbeHostIdentity? Host = null);

    private static readonly SemaphoreSlim Admission = new(1, 1);
    private readonly string marker = Marker(paths);
    private readonly Guid operationId = Guid.NewGuid();
    private Process? worker;
    private SafeFileHandle? job;
    private Task? drain;
    private bool ownsMarker;
    private bool UsesHost => OperatingSystem.IsLinux() && mode.Mode == ServerMode.System;

    internal async Task<ProxyRuntimeDto> DetectAsync(string engineId, string executable, CancellationToken ct)
    {
        if (engineId != MihomoEngine.Id)
            return new(engineId, ProxyRuntimeMode.None, ProxyRuntimeState.Failed, null, null, false, false, ProxyProblemCodes.NotSupported);
        if (!MihomoVersionWorker.TryResolveExternalExecutable(executable, out var path)) return Invalid();
        ct.ThrowIfCancellationRequested();
        if (!await Admission.WaitAsync(0, ct)) return Invalid(ProxyProblemCodes.RecoveryRequired);
        try
        {
            if (!TryReserve())
            {
                if (UsesHost) await ReconcileAsync(ct);
                return Invalid(ProxyProblemCodes.RecoveryRequired);
            }
            var version = UsesHost ? await ProbeHostedAsync(path, ct) : await ProbeAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            return string.IsNullOrEmpty(version) ? Invalid()
                : new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Stopped, version, null, false, true);
        }
        finally { Admission.Release(); }
    }

    private async Task<string?> ProbeHostedAsync(string executable, CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(MihomoVersionWorker.HostEndpoint(operationId));
        listener.Listen(1);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(7));
        var started = await helper.ExecuteAsync(new(PrivilegedOperationKind.MihomoProbeHostStart, OperationId: operationId), budget.Token);
        if (!started.Success || started.MihomoProbeHost is not { } original || original.OperationId != operationId
            || original.Pid <= 1 || original.BootId == Guid.Empty || original.InvocationId == Guid.Empty || original.StartTicks == 0)
            throw new IOException("The fixed external probe dispatch was not confirmed.");
        PersistDispatch(new(operationId, original.Pid, Host: original));

        using var socket = await listener.AcceptAsync(budget.Token);
        var peer = MihomoVersionWorker.HostPeer(socket);
        if (peer.Pid != original.Pid || peer.Uid != mode.Describe().ExecutionIdentity.Uid
            || MihomoVersionWorker.ReadStartTicks(original.Pid) != original.StartTicks)
            throw new IOException("The fixed external probe connection did not match its original worker.");
        using var stream = new NetworkStream(socket);
        using var input = new StreamReader(stream, leaveOpen: true);
        await using var output = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var response = input.ReadToEndAsync();
        var cancelled = false;
        try
        {
            var request = JsonSerializer.Serialize(new MihomoVersionWorker.Request(operationId, executable));
            await output.WriteLineAsync(request.AsMemory(), budget.Token);
            await response.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            cancelled = true;
            socket.Shutdown(SocketShutdown.Send);
        }
        await response.WaitAsync(TimeSpan.FromSeconds(12));
        var version = cancelled ? null : ReadWorkerVersion(await response);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await TryCompleteHostedAsync(original, cleanup.Token))
            {
                ClearMarker();
                return version;
            }
            await Task.Delay(75, cleanup.Token);
        }
        throw new IOException("The fixed external probe exit and unit cleanup remain unverified.");
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            using var input = File.OpenRead(marker);
            var dispatch = input.Length <= 1024 ? JsonSerializer.Deserialize<Dispatch>(input) : null;
            if (dispatch?.Host is not { } original || original.OperationId != dispatch.OperationId) return;
            if (await TryCompleteHostedAsync(original, ct)) ClearMarker();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private async Task<bool> TryCompleteHostedAsync(MihomoProbeHostIdentity original, CancellationToken ct)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.MihomoProbeHostComplete, OperationId: original.OperationId), ct);
        return result.Success && result.MihomoProbeExit is { } exit && exit.OperationId == original.OperationId
            && exit.BootId == original.BootId && exit.InvocationId == original.InvocationId
            && exit.Pid == original.Pid && exit.StartTicks == original.StartTicks;
    }

    private async Task<string?> ProbeAsync(string executable, CancellationToken ct)
    {
        var host = Environment.ProcessPath ?? throw new IOException("The Server executable path is unavailable.");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        TrustedProcessEnvironment.Apply(start);
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(MihomoVersionWorker).Assembly.Location);
        start.ArgumentList.Add("mihomo-version-worker");
        if (OperatingSystem.IsWindows()) job = WindowsProcessTreeJob.Create();
        var candidate = new Process { StartInfo = start };
        try
        {
            if (!candidate.Start()) { candidate.Dispose(); ClearMarker(); return null; }
        }
        catch (Win32Exception) { candidate.Dispose(); ClearMarker(); return null; }
        worker = candidate;
        return await DrainWorkerAsync(candidate, executable, ct);
    }

    private async Task<string?> DrainWorkerAsync(Process process, string executable, CancellationToken ct)
    {
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        drain = Task.WhenAll(process.WaitForExitAsync(), output, errors);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var cancelled = false;
        try
        {
            if (OperatingSystem.IsWindows()) WindowsProcessTreeJob.Attach(job!, process);
            PersistDispatch(new(operationId, process.Id, process.StartTime.ToUniversalTime()));
            var request = new MihomoVersionWorker.Request(operationId, executable);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), budget.Token);
            await process.StandardInput.FlushAsync(budget.Token);
            await drain.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { cancelled = true; }
        finally { process.StandardInput.Close(); }

        if (OperatingSystem.IsWindows())
            await WindowsProcessTreeJob.ConfirmExitAsync(job!, CancellationToken.None);
        await drain.WaitAsync(TimeSpan.FromSeconds(12));
        if (!OperatingSystem.IsWindows() && process.ExitCode is not (0 or MihomoVersionWorker.NoDispatch))
            throw new IOException("The external Mihomo worker did not confirm process tree cleanup.");
        var version = cancelled || process.ExitCode != 0 ? null : ReadWorkerVersion(await output);
        ClearMarker();
        return version;
    }

    private string? ReadWorkerVersion(string output)
    {
        try
        {
            var result = JsonSerializer.Deserialize<MihomoVersionWorker.Result>(output, MihomoVersionWorker.WireJson);
            if (result?.OperationId == operationId) return result.Version;
        }
        catch (JsonException error) { throw new IOException("The external Mihomo worker reply is invalid.", error); }
        throw new IOException("The external Mihomo worker reply does not match the original operation.");
    }

    private bool TryReserve()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        try
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            ownsMarker = true;
            WriteRecord(stream, new(operationId));
            SyncDirectory();
            return true;
        }
        catch (IOException) when (!ownsMarker && InspectResponsibility(paths) is not null) { return false; }
    }

    private void ClearMarker()
    {
        File.Delete(marker);
        SyncDirectory();
        ownsMarker = false;
    }

    private void PersistDispatch(Dispatch dispatch)
    {
        var staging = marker + ".dispatch";
        using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            WriteRecord(stream, dispatch);
        File.Move(staging, marker, overwrite: true);
        SyncDirectory();
    }

    private static void WriteRecord(FileStream stream, Dispatch record)
    {
        JsonSerializer.Serialize(stream, record);
        stream.Flush(flushToDisk: true);
    }

    private void SyncDirectory()
    {
        if (OperatingSystem.IsLinux()) LinuxDirectorySync.Flush(Path.GetDirectoryName(marker)!);
    }

    internal static string? InspectResponsibility(IProxyPlatformPaths paths)
    {
        try
        {
            using var stream = new FileStream(Marker(paths), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var operation = stream.Length <= 1024 ? JsonSerializer.Deserialize<Dispatch>(stream) : null;
            return operation is { OperationId: var id } && id != Guid.Empty
                ? $"External Mihomo version probe {id:D} requires cleanup verification."
                : "External Mihomo version probe ownership is incomplete.";
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return "External Mihomo version probe ownership could not be inspected."; }
    }

    private static string Marker(IProxyPlatformPaths paths) => Path.Combine(paths.GetStateDirectory(), "mihomo-version-probe.pending");
    private static ProxyRuntimeDto Invalid(string problem = ProxyProblemCodes.ExternalRuntimeInvalid) =>
        new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Failed, null, null, false, true, problem);

    public ValueTask DisposeAsync()
    {
        if (ownsMarker) throw new IOException("External Mihomo probe cleanup remains unverified.");
        worker?.Dispose();
        job?.Dispose();
        return ValueTask.CompletedTask;
    }
}
