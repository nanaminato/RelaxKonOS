using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Cross-process native mutation lock and immutable Helper-owned intent/result evidence.</summary>
internal sealed class TailscaleControlOperations(string root, Action<string> protect, ITailscaleNativeControl native)
{
    internal static Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request) =>
        new TailscaleControlOperations(TailscaleControlProtection.Root, TailscaleControlProtection.PrepareRoot, new TailscaleNativeControl()).HandleAsync(request);

    internal async Task<PrivilegedOperationResult> HandleAsync(PrivilegedOperationRequest request)
    {
        var execute = request.Operation == PrivilegedOperationKind.TailscaleControlExecute;
        var close = request.Operation == PrivilegedOperationKind.TailscaleRecoveryClose;
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, Version: request.Version, TailscaleChange: execute ? request.TailscaleChange : null, TailscaleRecovery: close ? request.TailscaleRecovery : null);
        if (request != clean || request.Operation is not (PrivilegedOperationKind.TailscaleControlRead or PrivilegedOperationKind.TailscaleControlExecute or PrivilegedOperationKind.TailscaleControlReceipt
                or PrivilegedOperationKind.TailscaleRecoveryRead or PrivilegedOperationKind.TailscaleRecoveryClose or PrivilegedOperationKind.TailscaleRecoveryReceipt)
            || request.Version != PrivilegedOperationProtocol.Version || request.OperationId is not { } id || id == Guid.Empty
            || request.Correlation?.IsValid() != true || request.Correlation.OperationId != id || execute && (request.TailscaleChange is not { } change
                || change.OperationId != id || !Valid(change))
            || close && (request.TailscaleRecovery is not { } recovery || recovery.Request is not { } closeRequest || closeRequest.OperationId != id
                || !closeRequest.Confirmed || closeRequest.ExpectedRevision is not { Length: 64 }
                || !closeRequest.ExpectedRevision.All(Uri.IsHexDigit) || !Guid.TryParseExact(recovery.Actor, "D", out var actor) || actor == Guid.Empty)) return Fail(PrivilegedProblemCode.InvalidRequest);
        try
        {
            protect(root);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var gate = await AcquireAsync(lifetime.Token);
            protect(root);
            if (request.Operation == PrivilegedOperationKind.TailscaleControlRead)
            {
                var snapshot = await native.ReadAsync(lifetime.Token);
                return new(true, TailscaleControl: HasUnresolved() ? snapshot with { CanControl = false, ProblemCode = "tailscale.recovery_required" } : snapshot);
            }
            var intent = Read<TailscalePrepareRequest>(id, "intent");
            if (request.Operation == PrivilegedOperationKind.TailscaleRecoveryReceipt)
                return new(true, TailscaleClosure: Read<TailscaleRecoveryClosure>(id, "closed"));
            if (request.Operation is PrivilegedOperationKind.TailscaleRecoveryRead or PrivilegedOperationKind.TailscaleRecoveryClose)
                return await RecoverAsync(request, intent, lifetime.Token);
            if (request.Operation == PrivilegedOperationKind.TailscaleControlReceipt)
                return new(true, TailscaleResult: intent is null ? null : Read<TailscaleControlResult>(id, "result")
                    ?? new(id, false, ProblemCode: "tailscale.operation.interrupted"));
            var target = request.TailscaleChange!;
            if (intent is not null)
            {
                if (intent != target) return Fail(PrivilegedProblemCode.Conflict);
                return new(true, TailscaleResult: Read<TailscaleControlResult>(id, "result")
                    ?? new(id, false, ProblemCode: "tailscale.operation.interrupted"));
            }
            // An interrupted old helper may have left a native command in flight. No new mutation may pass it.
            if (HasUnresolved()) return Fail(PrivilegedProblemCode.Conflict);
            var baseline = await native.ReadAsync(lifetime.Token);
            if (!baseline.CanControl || baseline.Revision != target.ExpectedRevision || target.Action == TailscaleAction.Connect && !baseline.Authenticated)
                return Fail(PrivilegedProblemCode.Conflict);
            Write(id, "intent", target);
            TailscaleControlResult result;
            try
            {
                var completed = await native.ApplyAsync(target, lifetime.Token);
                var observed = await native.ReadAsync(lifetime.Token);
                var matches = target.Action switch
                {
                    TailscaleAction.Connect => observed.Connected && observed.Authenticated && observed.Configuration == baseline.Configuration,
                    TailscaleAction.Disconnect => !observed.Connected && observed.Configuration == baseline.Configuration,
                    TailscaleAction.Configure => observed.Configuration == target.Configuration && observed.Connected == baseline.Connected,
                    _ => false
                };
                result = completed && matches ? new(id, true, observed) : new(id, false, ProblemCode: "tailscale.operation.outcome_unknown");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or OperationCanceledException
                or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { result = new(id, false, ProblemCode: "tailscale.operation.outcome_unknown"); }
            Write(id, "result", result);
            return new(true, TailscaleResult: result);
        }
        catch (UnauthorizedAccessException) { return Fail(PrivilegedProblemCode.AccessDenied); }
        catch (PlatformNotSupportedException) { return Fail(PrivilegedProblemCode.UnsupportedOperation); }
        catch (OperationCanceledException) { return Fail(PrivilegedProblemCode.TimedOut); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Fail(PrivilegedProblemCode.InternalError); }
    }

    private async Task<PrivilegedOperationResult> RecoverAsync(PrivilegedOperationRequest request, TailscalePrepareRequest? intent, CancellationToken ct)
    {
        var id = request.OperationId!.Value;
        if (intent is null || Read<TailscaleControlResult>(id, "result")?.Completed == true) return Fail(PrivilegedProblemCode.Conflict);
        var closure = Read<TailscaleRecoveryClosure>(id, "closed");
        if (closure is not null)
        {
            if (request.TailscaleRecovery is { } repeated && (repeated.Actor != closure.Actor || repeated.Request.ExpectedRevision != closure.Observation.Revision))
                return Fail(PrivilegedProblemCode.Conflict);
            return new(true, TailscaleRecovery: new(id, closure.Observation, false, closure), TailscaleClosure: closure);
        }
        // Never close while an orphaned CLI may still be writing. Reads themselves are bounded and finish before rechecking.
        if (!await native.IsQuiescentAsync(ct)) return RecoveryBlocked(id, "tailscale.recovery.cli_active");
        var before = await native.ReadAsync(ct);
        var after = await native.ReadAsync(ct);
        if (before != after || !after.CanControl || after.Revision is not { Length: 64 } || !after.Revision.All(Uri.IsHexDigit) || !await native.IsQuiescentAsync(ct))
            return RecoveryBlocked(id, "tailscale.recovery.observation_changed");
        if (request.Operation == PrivilegedOperationKind.TailscaleRecoveryRead)
            return new(true, TailscaleRecovery: new(id, after, true));
        var command = request.TailscaleRecovery!;
        if (command.Request.ExpectedRevision != after.Revision) return Fail(PrivilegedProblemCode.Conflict);
        closure = new(id, after, DateTimeOffset.UtcNow, command.Actor);
        // A separate immutable acknowledgement. Intent/result evidence is never overwritten or manufactured.
        Write(id, "closed", closure);
        return new(true, TailscaleClosure: closure);
    }
    private static PrivilegedOperationResult RecoveryBlocked(Guid id, string problem) =>
        new(true, TailscaleRecovery: new(id, null, false, ProblemCode: problem));

    private bool HasUnresolved()
    {
        foreach (var pending in Directory.EnumerateFiles(root, "*.intent.json"))
        {
            var id = Guid.ParseExact(Path.GetFileName(pending)[..32], "N");
            if (Read<TailscalePrepareRequest>(id, "intent") is null || Read<TailscaleControlResult>(id, "result")?.Completed != true
                && Read<TailscaleRecoveryClosure>(id, "closed") is null) return true;
        }
        return false;
    }

    private static bool Valid(TailscalePrepareRequest change) => Enum.IsDefined(change.Action)
        && change.ExpectedRevision is { Length: 64 } && change.ExpectedRevision.All(Uri.IsHexDigit)
        && (change.Action == TailscaleAction.Configure) == (change.Configuration is not null);
    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
    private async Task<FileStream> AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(root, "native.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(50, ct); }
        }
    }
    private string PathFor(Guid id, string suffix) => Path.Combine(root, id.ToString("N") + "." + suffix + ".json");
    private T? Read<T>(Guid id, string suffix) where T : class
    {
        var path = PathFor(id, suffix);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new IOException("tailscale.receipt_invalid");
        var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new IOException("tailscale.receipt_invalid");
        if (value is TailscalePrepareRequest intent && (intent.OperationId != id || !Valid(intent))
            || value is TailscaleControlResult result && result.OperationId != id
            || value is TailscaleRecoveryClosure closure && (closure.OperationId != id || !Guid.TryParseExact(closure.Actor, "D", out var closureActor) || closureActor == Guid.Empty
                || closure.ClosedAt == default || closure.Observation is null || closure.Observation.Configuration is null || !closure.Observation.CanControl
                || closure.Observation.Revision is not { Length: 64 } || !closure.Observation.Revision.All(Uri.IsHexDigit))) throw new IOException("tailscale.receipt_identity");
        return value;
    }
    private void Write<T>(Guid id, string suffix, T value)
    {
        var path = PathFor(id, suffix);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options)) { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temp, path, overwrite: false);
            if (OperatingSystem.IsLinux()) TailscaleControlProtection.FlushDirectory(root);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
