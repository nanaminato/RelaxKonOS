using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed partial class TailscaleControlOperations
{
    private TailscaleNativeLogin Login => login ?? new TailscaleNativeLogin();
    private DateTimeOffset LoginNow => (timeProvider ?? TimeProvider.System).GetUtcNow();
    private static readonly JsonSerializerOptions LoginJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidLogin(TailscaleLoginIntent value) => value.OperationId != Guid.Empty && value.HostId != Guid.Empty
        && value.Actor != Guid.Empty && value.SessionId != Guid.Empty && Digest(value.ExpectedRevision) && value.ExpiresAt != default;
    private static InvalidDataException LoginConflict() => new("tailscale.login.conflict");
    private static TailscaleLoginObservation Observation(TailscaleLoginBaseline baseline) =>
        new(baseline.Revision, baseline.PreferencesRevision, baseline.BackendState, baseline.CanStart);

    // The Server must supply freshly authorized, confirmed identity and plan fields.
    internal async Task<TailscaleLoginReceipt> BeginLoginAsync(TailscaleLoginIntent target, bool confirmed, CancellationToken ct)
    {
        if (!confirmed || !ValidLogin(target)) throw LoginConflict();
        protect(root);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(ct);
        admission.CancelAfter(TimeSpan.FromSeconds(45));
        using var gate = await AcquireAsync(admission.Token);
        protect(root);
        var existing = ReadLoginIntent(target.OperationId);
        if (existing is not null)
        {
            if (existing != target) throw LoginConflict();
            // Missing original result is unknown, even if the daemon is now Running. Never replay.
            return ReadLoginReceipt(target.OperationId) ?? new(target.OperationId, TailscaleLoginState.Unknown, LoginNow);
        }
        // Orphaned evidence for this id is never overwritten or reused, even when its intent is missing.
        if (target.ExpiresAt <= LoginNow || target.ExpiresAt > LoginNow.AddMinutes(5)
            || File.Exists(PathFor(target.OperationId, "intent")) || File.Exists(PathFor(target.OperationId, "login-result"))
            || File.Exists(PathFor(target.OperationId, "login-closed")) || HasUnresolved()
            || !await native.IsQuiescentAsync(admission.Token)) throw LoginConflict();
        var baseline = await Login.ReadAsync(admission.Token);
        if (!baseline.CanStart || baseline.Revision != target.ExpectedRevision || target.ExpiresAt <= LoginNow) throw LoginConflict();
        admission.Token.ThrowIfCancellationRequested();
        Write(target.OperationId, "login-intent", target);
        // Once intent is durable, caller cancellation stops waiting, not the submitted native action.
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        TailscaleLoginReceipt receipt;
        try
        {
            using var result = await Login.BeginAsync(target.ExpectedRevision, lifetime.Token);
            var observed = result.Observation ?? throw LoginConflict();
            var url = result.TakeAuthenticationUrl();
            var fingerprint = result.State == "awaiting-browser"
                ? TailscaleNativeLogin.FingerprintChallenge(url ?? throw LoginConflict()) : null;
            receipt = new(target.OperationId, result.State switch
            {
                "awaiting-browser" => TailscaleLoginState.Issued,
                "completed" => TailscaleLoginState.Completed,
                "approval-required" => TailscaleLoginState.ApprovalRequired,
                _ => throw LoginConflict()
            }, LoginNow, observed.PreferencesRevision, observed.Revision, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or OperationCanceledException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { receipt = new(target.OperationId, TailscaleLoginState.Unknown, LoginNow); }
        Write(target.OperationId, "login-result", receipt);
        return receipt;
    }

    internal async Task<TailscaleLoginReceipt?> ReadLoginReceiptAsync(TailscaleLoginIntent identity, CancellationToken ct)
    {
        if (!ValidLogin(identity)) throw LoginConflict();
        protect(root);
        using var gate = await AcquireAsync(ct);
        protect(root);
        var intent = ReadLoginIntent(identity.OperationId);
        if (intent is null) return null;
        if (intent != identity) throw LoginConflict();
        return ReadLoginReceipt(identity.OperationId) ?? new(identity.OperationId, TailscaleLoginState.Unknown, LoginNow);
    }

    internal async Task<TailscaleLoginNativeResult> ReadLoginChallengeAsync(TailscaleLoginIntent identity, CancellationToken ct)
    {
        if (!ValidLogin(identity)) throw LoginConflict();
        protect(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        using var gate = await AcquireAsync(deadline.Token);
        protect(root);
        var intent = ReadLoginIntent(identity.OperationId);
        var receipt = ReadLoginReceipt(identity.OperationId);
        if (intent is null || intent != identity || intent.ExpiresAt <= LoginNow || receipt?.State != TailscaleLoginState.Issued
            || ReadLoginClosure(intent) is not null || !await native.IsQuiescentAsync(deadline.Token)) throw LoginConflict();
        var result = await Login.ReadChallengeAsync(receipt.ChallengeFingerprint!, receipt.PreferencesRevision!, deadline.Token);
        if (intent.ExpiresAt <= LoginNow || deadline.IsCancellationRequested)
        { result.Dispose(); throw LoginConflict(); }
        return result;
    }

    internal async Task<TailscaleLoginObservation> ReadLoginRecoveryAsync(TailscaleLoginIntent identity, CancellationToken ct)
    {
        if (!ValidLogin(identity)) throw LoginConflict();
        protect(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        using var gate = await AcquireAsync(deadline.Token);
        protect(root);
        if (ReadLoginIntent(identity.OperationId) != identity || ReadLoginReceipt(identity.OperationId)?.State == TailscaleLoginState.Completed)
            throw LoginConflict();
        var closure = ReadLoginClosure(identity);
        return closure?.Observation ?? Observation(await ObserveStableLoginAsync(deadline.Token));
    }

    internal async Task<TailscaleLoginClosure?> ReadLoginClosureAsync(TailscaleLoginIntent identity, CancellationToken ct)
    {
        if (!ValidLogin(identity)) throw LoginConflict();
        protect(root);
        using var gate = await AcquireAsync(ct);
        protect(root);
        if (ReadLoginIntent(identity.OperationId) != identity) throw LoginConflict();
        return ReadLoginClosure(identity);
    }

    internal async Task<TailscaleLoginClosure> CloseLoginAsync(TailscaleLoginIntent identity, string expectedRevision, bool confirmed, CancellationToken ct)
    {
        if (!confirmed || !ValidLogin(identity) || !Digest(expectedRevision)) throw LoginConflict();
        protect(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        using var gate = await AcquireAsync(deadline.Token);
        protect(root);
        if (ReadLoginIntent(identity.OperationId) != identity || ReadLoginReceipt(identity.OperationId)?.State == TailscaleLoginState.Completed)
            throw LoginConflict();
        var existing = ReadLoginClosure(identity);
        if (existing is not null)
            return existing.Observation.Revision == expectedRevision ? existing : throw LoginConflict();
        var observation = Observation(await ObserveStableLoginAsync(deadline.Token));
        if (observation.Revision != expectedRevision) throw LoginConflict();
        deadline.Token.ThrowIfCancellationRequested();
        var closure = new TailscaleLoginClosure(identity, observation, LoginNow);
        Write(identity.OperationId, "login-closed", closure);
        return closure;
    }

    private async Task<TailscaleLoginBaseline> ObserveStableLoginAsync(CancellationToken ct)
    {
        if (!await native.IsQuiescentAsync(ct)) throw LoginConflict();
        var before = await Login.ReadAsync(ct);
        var after = await Login.ReadAsync(ct);
        if (before != after || !await native.IsQuiescentAsync(ct)) throw LoginConflict();
        return after;
    }

    private TailscaleLoginClosure? ReadLoginClosure(TailscaleLoginIntent identity)
    {
        var value = ReadLogin<TailscaleLoginClosure>(identity.OperationId, "login-closed");
        if (value is not null && (value.Intent != identity || value.ClosedAt == default || value.Observation is not { } observed
            || !Digest(observed.Revision) || !Digest(observed.PreferencesRevision)
            || observed.BackendState is not ("NeedsLogin" or "NeedsMachineAuth" or "Running" or "Stopped")
            || observed.CanStart && observed.BackendState != "NeedsLogin")) throw LoginConflict();
        return value;
    }

    private bool HasUnresolvedLogin()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.login-intent.json"))
        {
            var name = Path.GetFileName(file);
            if (name.Length != 32 + ".login-intent.json".Length || !Guid.TryParseExact(name[..32], "N", out var id)) return true;
            var intent = ReadLoginIntent(id);
            if (intent is null || ReadLoginReceipt(id)?.State != TailscaleLoginState.Completed && ReadLoginClosure(intent) is null) return true;
        }
        return false;
    }

    private TailscaleLoginIntent? ReadLoginIntent(Guid id)
    {
        var value = ReadLogin<TailscaleLoginIntent>(id, "login-intent");
        if (value is not null && (!ValidLogin(value) || value.OperationId != id)) throw LoginConflict();
        return value;
    }
    private TailscaleLoginReceipt? ReadLoginReceipt(Guid id)
    {
        var value = ReadLogin<TailscaleLoginReceipt>(id, "login-result");
        if (value is null) return null;
        if (value.OperationId != id || value.RecordedAt == default || value.State switch
            {
                TailscaleLoginState.Unknown => value.PreferencesRevision is not null || value.ObservedRevision is not null || value.ChallengeFingerprint is not null,
                TailscaleLoginState.Issued => !Digest(value.PreferencesRevision) || !Digest(value.ObservedRevision) || !Digest(value.ChallengeFingerprint),
                TailscaleLoginState.Completed or TailscaleLoginState.ApprovalRequired =>
                    !Digest(value.PreferencesRevision) || !Digest(value.ObservedRevision) || value.ChallengeFingerprint is not null,
                _ => true
            }) throw LoginConflict();
        return value;
    }
    private T? ReadLogin<T>(Guid id, string suffix) where T : class
    {
        var path = PathFor(id, suffix);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw LoginConflict();
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        TailscaleNativeControl.Canonical(document.RootElement); // Reject duplicate properties before binding evidence.
        return JsonSerializer.Deserialize<T>(bytes, LoginJson) ?? throw LoginConflict();
    }
}
