using System.Collections.Concurrent;
using System.Net;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Temporary port-80 responder used only while a Direct HTTP-01 order is active.
/// It exposes no application routes and releases the listener once the last token is removed.</summary>
internal sealed class DirectHttp01ChallengeStore : IHttp01ChallengeStore, IDisposable
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _stopping;

    public Task PutAsync(string token, string keyAuthorization, CancellationToken cancellationToken)
    {
        if (!FileHttp01ChallengeStoreToken.IsValid(token) || string.IsNullOrWhiteSpace(keyAuthorization) || keyAuthorization.Length > 4096 || keyAuthorization.Any(char.IsControl))
            throw new CertificateOperationException("certificate.challenge_invalid");
        EnsureListening();
        _tokens[token] = keyAuthorization;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string token, CancellationToken cancellationToken)
    {
        _tokens.TryRemove(token, out _);
        if (_tokens.IsEmpty) Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => Stop();

    private void EnsureListening()
    {
        lock (_gate)
        {
            if (_listener is { IsListening: true }) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://+:80/.well-known/acme-challenge/");
                _listener.Start();
                _stopping = new CancellationTokenSource();
                _ = ServeAsync(_listener, _stopping.Token);
            }
            catch (HttpListenerException) { Stop(); throw new CertificateOperationException("certificate.port80_unavailable"); }
            catch (PlatformNotSupportedException) { Stop(); throw new CertificateOperationException("certificate.direct_http01_unsupported"); }
        }
    }

    private async Task ServeAsync(HttpListener listener, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().WaitAsync(stopping); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) when (stopping.IsCancellationRequested) { break; }
                _ = WriteResponseAsync(context, stopping);
            }
        }
        catch { /* The ACME operation surfaces listener creation failures; serving faults only stop this temporary listener. */ }
    }

    private async Task WriteResponseAsync(HttpListenerContext context, CancellationToken stopping)
    {
        try
        {
            var prefix = "/.well-known/acme-challenge/";
            var path = context.Request.Url?.AbsolutePath ?? "";
            var token = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : "";
            if (context.Request.HttpMethod != "GET" || !FileHttp01ChallengeStoreToken.IsValid(token) || !_tokens.TryGetValue(token, out var value))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            var body = System.Text.Encoding.ASCII.GetBytes(value);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, stopping);
        }
        catch { }
        finally { context.Response.Close(); }
    }

    private void Stop()
    {
        lock (_gate)
        {
            _stopping?.Cancel();
            _stopping?.Dispose();
            _stopping = null;
            _listener?.Close();
            _listener = null;
        }
    }
}

internal static partial class FileHttp01ChallengeStoreToken
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]{1,256}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Pattern();
    public static bool IsValid(string token) => Pattern().IsMatch(token);
}
