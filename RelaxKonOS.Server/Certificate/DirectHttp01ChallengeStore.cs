using System.Collections.Concurrent;
using System.Net;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Temporary port-80 responder. Each generation owns its actual accept and response tasks.</summary>
internal sealed class DirectHttp01ChallengeStore : IHttp01ChallengeStore, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _prefix;
    private readonly Func<HttpListenerContext, CancellationToken, Task>? _response;
    private ListenerGeneration? _generation;
    private Exception? _failure;
    private HttpListener? _failedListener;
    private bool _disposed;

    public DirectHttp01ChallengeStore() : this("http://+:80/.well-known/acme-challenge/") { }
    // Internal listener/transport seam: production always uses the fixed public port-80 constructor.
    internal DirectHttp01ChallengeStore(string prefix, Func<HttpListenerContext, CancellationToken, Task>? response = null)
    { _prefix = prefix; _response = response; }

    public async Task PutAsync(string token, string keyAuthorization, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!FileHttp01ChallengeStoreToken.IsValid(token) || string.IsNullOrWhiteSpace(keyAuthorization)
            || keyAuthorization.Length > 4096 || keyAuthorization.Any(char.IsControl))
            throw new CertificateOperationException("certificate.challenge_invalid");
        while (true)
        {
            Task? previous = null;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failure is not null) throw new CertificateOperationException("certificate.port80_unavailable");
                if (_generation is { IsClosing: true } closing) previous = closing.Completion;
                else
                {
                    var created = _generation is null;
                    var generation = _generation ??= CreateListener();
                    generation.Tokens[token] = keyAuthorization;
                    if (created) generation.Start();
                    return;
                }
            }
            await previous!.WaitAsync(cancellationToken);
        }
    }

    public async Task RemoveAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListenerGeneration? generation;
        Exception? failure;
        var close = false;
        lock (_gate)
        {
            generation = _generation;
            failure = _failure;
            if (generation is not null)
            {
                generation.Tokens.TryRemove(token, out _);
                if (generation.Tokens.IsEmpty) close = generation.MarkClosing();
                else if (failure is null && !generation.IsClosing) return;
            }
        }
        if (generation is null)
        {
            if (failure is not null) throw failure;
            return;
        }
        if (close) generation.CloseListener();
        await generation.Completion.WaitAsync(cancellationToken);
    }

    private ListenerGeneration CreateListener()
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(_prefix);
            listener.Start();
            return new(this, listener, _response);
        }
        catch (Exception error)
        {
            try { listener.Close(); }
            catch (Exception cleanup) { _failure = cleanup; _failedListener = listener; }
            if (error is HttpListenerException) throw new CertificateOperationException("certificate.port80_unavailable");
            if (error is PlatformNotSupportedException) throw new CertificateOperationException("certificate.direct_http01_unsupported");
            throw;
        }
    }

    private Task BeginDispose()
    {
        ListenerGeneration? generation;
        bool close;
        lock (_gate)
        {
            _disposed = true; generation = _generation;
            close = generation?.MarkClosing() == true;
        }
        if (close) generation!.CloseListener();
        if (_failedListener is not null) return Task.FromException(_failure!);
        return generation?.Completion ?? (_failure is null ? Task.CompletedTask : Task.FromException(_failure));
    }
    public void Dispose() => _ = BeginDispose();
    public ValueTask DisposeAsync() => new(BeginDispose());

    private void Completed(ListenerGeneration generation, Exception? failure)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_generation, generation)) return;
            if (failure is null) _generation = null;
            else _failure = failure;
        }
    }

    private sealed class ListenerGeneration(DirectHttp01ChallengeStore owner, HttpListener listener,
        Func<HttpListenerContext, CancellationToken, Task>? response)
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _runner;
        private int _closing;
        private Exception? _closeFailure;
        internal ConcurrentDictionary<string, string> Tokens { get; } = new(StringComparer.Ordinal);
        internal bool IsClosing => Volatile.Read(ref _closing) != 0;
        internal Task Completion => _done.Task;
        internal void Start() => _runner = ServeAsync();
        internal bool MarkClosing() => Interlocked.Exchange(ref _closing, 1) == 0;
        internal void CloseListener()
        {
            try { _stopping.Cancel(); }
            catch (Exception error) { _closeFailure = error; }
            try { listener.Close(); }
            catch (Exception error) { _closeFailure ??= error; }
            finally { _closed.TrySetResult(); }
        }
        private async Task ServeAsync()
        {
            Exception? failure = null;
            try
            {
                // Serial response execution bounds live accepted response work to one per listener generation.
                while (!IsClosing)
                {
                    var context = await listener.GetContextAsync();
                    try
                    {
                        if (response is null) await WriteResponseAsync(context);
                        else await response(context, _stopping.Token);
                    }
                    finally { context.Response.Close(); }
                }
            }
            catch (Exception error) when (IsClosing && (error is HttpListenerException or ObjectDisposedException or OperationCanceledException or IOException)) { }
            catch (Exception error) { failure = error; }
            finally
            {
                if (MarkClosing()) CloseListener();
                await _closed.Task;
                failure ??= _closeFailure;
                _stopping.Dispose();
                if (failure is null) Tokens.Clear();
                owner.Completed(this, failure);
                if (failure is null) _done.TrySetResult();
                else { _done.TrySetException(failure); _ = _done.Task.Exception; }
            }
        }
        private async Task WriteResponseAsync(HttpListenerContext context)
        {
            const string prefix = "/.well-known/acme-challenge/";
            var path = context.Request.Url?.AbsolutePath ?? "";
            var token = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : "";
            if (context.Request.HttpMethod != "GET" || !FileHttp01ChallengeStoreToken.IsValid(token) || !Tokens.TryGetValue(token, out var value))
            { context.Response.StatusCode = (int)HttpStatusCode.NotFound; return; }
            var body = System.Text.Encoding.ASCII.GetBytes(value);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, _stopping.Token);
        }
    }
}

internal static partial class FileHttp01ChallengeStoreToken
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]{1,256}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Pattern();
    public static bool IsValid(string token) => Pattern().IsMatch(token);
}
