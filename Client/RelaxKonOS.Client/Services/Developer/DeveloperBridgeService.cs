using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>
/// A localhost-only bridge for development tooling. It intentionally has no LAN listener and
/// requires the pairing token shown in Settings before accepting any command.
/// </summary>
public sealed class DeveloperBridgeService : IDisposable
{
    private const string TokenHeader = "X-RemoteOS-Dev-Token";
    private readonly DeveloperModeService _mode;
    private readonly DeveloperPackageManager _packages;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;

    public DeveloperBridgeService(DeveloperModeService mode, DeveloperPackageManager packages)
    {
        _mode = mode;
        _packages = packages;
        _mode.Changed += OnModeChanged;
        Synchronize();
    }

    public bool IsRunning
    {
        get { lock (_gate) return _listener?.IsListening == true; }
    }

    public void Dispose()
    {
        _mode.Changed -= OnModeChanged;
        Stop();
    }

    private void OnModeChanged(object? sender, EventArgs eventArgs) => Synchronize();

    private void Synchronize()
    {
        if (_mode.IsEnabled)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_listener?.IsListening == true)
                return;

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{DeveloperModeService.BridgePort}/");
            try { listener.Start(); }
            catch (HttpListenerException)
            {
                listener.Close();
                return;
            }

            _listener = listener;
            _cancellation = new CancellationTokenSource();
            _ = ListenAsync(listener, _cancellation.Token);
        }
    }

    private void Stop()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _listener?.Close();
            _listener = null;
        }
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            try
            {
                var context = await listener.GetContextAsync();
                _ = ProcessAsync(context, cancellationToken);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !listener.IsListening) { }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    private async Task ProcessAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!_mode.IsEnabled)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Forbidden, new { error = "Developer Mode is disabled." });
                return;
            }
            if (!HasValidToken(context.Request.Headers[TokenHeader]))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Unauthorized, new { error = "Pair with RelaxKonOS Developer Mode first." });
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (context.Request.HttpMethod == "GET" && path == "/api/developer/v1.0/apps")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, _packages.Installed);
                return;
            }
            if (context.Request.HttpMethod == "POST" && path == "/api/developer/v1.0/packages")
            {
                var launch = !string.Equals(context.Request.QueryString["launch"], "false", StringComparison.OrdinalIgnoreCase);
                var app = await _packages.InstallAsync(context.Request.InputStream, launch, cancellationToken);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, app);
                return;
            }

            const string appPrefix = "/api/developer/v1.0/apps/";
            if (path.StartsWith(appPrefix, StringComparison.Ordinal))
            {
                var remainder = path[appPrefix.Length..];
                if (remainder.EndsWith("/launch", StringComparison.Ordinal) && context.Request.HttpMethod == "POST")
                {
                    var appId = Uri.UnescapeDataString(remainder[..^"/launch".Length]);
                    var launched = await _packages.LaunchAsync(appId);
                    await WriteJsonAsync(context.Response, launched ? HttpStatusCode.OK : HttpStatusCode.NotFound, new { launched });
                    return;
                }
                if (context.Request.HttpMethod == "DELETE" && !remainder.Contains('/'))
                {
                    var removed = await _packages.UninstallAsync(Uri.UnescapeDataString(remainder));
                    await WriteJsonAsync(context.Response, removed ? HttpStatusCode.OK : HttpStatusCode.NotFound, new { removed });
                    return;
                }
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "Unknown Developer Bridge route." });
        }
        catch (InvalidOperationException exception)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = exception.Message });
        }
        catch (Exception)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.InternalServerError, new { error = "Developer Bridge command failed." });
        }
    }

    private bool HasValidToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var expected = Encoding.UTF8.GetBytes(_mode.PairingToken);
        var actual = Encoding.UTF8.GetBytes(value);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object value)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var payload = JsonSerializer.Serialize(value);
        await using var writer = new StreamWriter(response.OutputStream, Encoding.UTF8, leaveOpen: false);
        await writer.WriteAsync(payload);
        response.Close();
    }
}
