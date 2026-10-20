using System.Text.Json;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// The one cancellation channel between two Helper processes. A transaction holds the instance lock for
/// its whole life, so a request can never be delivered through that lock: it is a single marker file whose
/// content is the exact operation id. A marker for another operation is ignored, so a stale file can never
/// abort an unrelated transaction, and the running transaction deletes the marker when it ends.
/// </summary>
internal static class RuntimeCancelChannel
{
    internal static string Marker(string root) => Path.Combine(root, "state", "cancel.request");

    /// <summary>Records a request. It deliberately reads no ledger state, so it cannot block behind the lock.</summary>
    internal static void Request(string root, Guid operationId)
    {
        var target = Marker(root);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".new";
        File.WriteAllText(temporary, operationId.ToString("N"));
        File.Move(temporary, target, true);
    }

    /// <summary>
    /// True only when the marker names this exact operation. An unreadable or malformed marker is treated as
    /// absent: a broken cancellation channel must never turn into an aborted root transaction.
    /// </summary>
    internal static bool Matches(string root, Guid operationId)
    {
        try
        {
            var target = Marker(root);
            if (!File.Exists(target)) return false;
            return Guid.TryParseExact(File.ReadAllText(target).Trim(), "N", out var requested) && requested == operationId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Best effort: a leftover marker cannot be honored by an ended transaction anyway.</summary>
    internal static void Clear(string root)
    {
        try { File.Delete(Marker(root)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Turns the marker into a token the transaction already understands. Polling is deliberate: it costs one
/// existence check per interval, works on both fixed hosts without a watcher quota, and leaves no thread
/// blocked on a file system notification that a root-only directory may not deliver.
/// </summary>
internal sealed class RuntimeCancellation : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private readonly string _root;
    private readonly CancellationTokenSource _source;
    private readonly Task _poll;

    private RuntimeCancellation(string root, Guid operationId, CancellationToken ct)
    {
        _root = root;
        _source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _poll = Task.Run(() => PollAsync(operationId));
    }

    internal CancellationToken Token => _source.Token;

    internal static RuntimeCancellation Watch(string root, Guid operationId, CancellationToken ct) =>
        new(root, operationId, ct);

    private async Task PollAsync(Guid operationId)
    {
        var token = _source.Token;
        while (!token.IsCancellationRequested)
        {
            if (RuntimeCancelChannel.Matches(_root, operationId))
            {
                try { await _source.CancelAsync(); } catch (ObjectDisposedException) { }
                return;
            }
            try { await Task.Delay(PollInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        try { _source.Cancel(); } catch (ObjectDisposedException) { }
        try { _poll.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _source.Dispose();
        RuntimeCancelChannel.Clear(_root);
    }
}
