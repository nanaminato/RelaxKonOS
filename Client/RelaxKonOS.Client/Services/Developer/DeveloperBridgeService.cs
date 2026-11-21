using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Core.VirtualSystemDrive;
using RelaxKonOS.Protocol.Developer;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>
/// A localhost-only bridge for development tooling. It intentionally has no LAN listener and
/// requires the pairing token shown in Settings before accepting any command.
/// </summary>
public sealed class DeveloperBridgeService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
            listener.Prefixes.Add($"http://127.0.0.1:{_mode.Port}/");
            try { listener.Start(); }
            catch (HttpListenerException exception)
            {
                System.Diagnostics.Trace.TraceWarning("Developer Bridge could not bind port {0}: {1}", _mode.Port, exception.Message);
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
                var context = await listener.GetContextAsync().ConfigureAwait(false);
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
                await WriteResultAsync(context.Response, new("developer_mode_disabled"));
                return;
            }
            if (!HasValidToken(context.Request.Headers[DeveloperBridgeTarget.TokenHeader]))
            {
                await WriteResultAsync(context.Response, new("unauthorized", Message: "Pair with RelaxKonOS Developer Mode first."));
                return;
            }
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (await TryProcessDirectAsync(context, path, cancellationToken)) return;
            var profile = context.Request.Headers[DeveloperBridgeTarget.ProfileHeader];
            if (profile is not null && profile != DeveloperProfile.Name)
            {
                await WriteResultAsync(context.Response, new("profile_mismatch"));
                return;
            }

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

            await WriteResultAsync(context.Response, new("route_not_found"));
        }
        catch (PackageStorageUnavailableException exception)
        {
            // A developer pushing packages from the CLI needs the same answer the installer shows:
            // which folder refused the write, not a generic bridge failure.
            await WriteJsonAsync(context.Response, HttpStatusCode.ServiceUnavailable,
                new { ok = false, error = exception.Message, code = exception.ProblemCode, folder = exception.Folder });
        }
        catch (InvalidOperationException exception)
        {
            await WriteResultAsync(context.Response, new("invalid_request", Message: exception.Message));
        }
        catch (OperationCanceledException)
        {
            await WriteResultAsync(context.Response, new("cancelled"));
        }
        catch (IOException exception)
        {
            await WriteResultAsync(context.Response, new("io_error", Message: exception.Message));
        }
        catch (Exception)
        {
            await WriteResultAsync(context.Response, new("internal_error", Message: "Developer Bridge command failed."));
        }
    }

    private async Task<bool> TryProcessDirectAsync(HttpListenerContext context, string path, CancellationToken cancellationToken)
    {
        const string prefix = DeveloperBridgeTarget.RoutePrefix;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = path[prefix.Length..].Split('/');
        var route = parts switch
        {
            ["status"] => "status",
            ["deploy"] => "deploy",
            ["apps", _, "diagnostics" or "reload" or "rollback"] => parts[2],
            _ => null,
        };
        if (route is null) return false;
        if (!DeveloperProfile.IsDebug)
        {
            await WriteResultAsync(context.Response, new("debug_required"));
            return true;
        }
        var profile = context.Request.Headers[DeveloperBridgeTarget.ProfileHeader];
        if (profile != DeveloperProfile.Name)
        {
            await WriteResultAsync(context.Response, new("profile_mismatch"));
            return true;
        }

        var appId = parts.Length == 3 ? Uri.UnescapeDataString(parts[1]) : null;
        var method = route is "status" or "diagnostics" ? "GET" : "POST";
        if (context.Request.HttpMethod != method)
        {
            await WriteResultAsync(context.Response, new("method_not_allowed"));
            return true;
        }
        if (method == "GET")
        {
            var apps = await _packages.StatusAsync(appId, cancellationToken);
            if (appId is not null && apps.Count == 0)
                await WriteResultAsync(context.Response, new("app_not_found", appId));
            else
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
                {
                    ok = true, code = "ok", profile = DeveloperProfile.Name, port = _mode.Port,
                    processId = Environment.ProcessId, apps,
                }, JsonOptions);
            return true;
        }

        var launchOption = context.Request.QueryString["launch"];
        var launch = true;
        if (launchOption is not null && !bool.TryParse(launchOption, out launch))
        {
            await WriteResultAsync(context.Response, new("invalid_request", Message: "launch must be true or false."));
            return true;
        }
        if (route == "deploy" && context.Request.ContentType != "application/x-tar")
        {
            await WriteResultAsync(context.Response, new("unsupported_media_type"));
            return true;
        }
        var result = route == "deploy"
            ? await _packages.DeployAsync(context.Request.InputStream, launch, cancellationToken)
            : await _packages.SwitchAsync(appId!, rollback: route == "rollback", launch, cancellationToken);
        await WriteResultAsync(context.Response, result);
        return true;
    }

    private static Task WriteResultAsync(HttpListenerResponse response, DeveloperOperationResult result) =>
        WriteJsonAsync(response, result.Code switch
        {
            "ok" => HttpStatusCode.OK,
            "unauthorized" => HttpStatusCode.Unauthorized,
            "debug_required" or "developer_mode_disabled" => HttpStatusCode.Forbidden,
            "app_not_found" or "route_not_found" => HttpStatusCode.NotFound,
            "method_not_allowed" => HttpStatusCode.MethodNotAllowed,
            "unsupported_media_type" => HttpStatusCode.UnsupportedMediaType,
            "profile_mismatch" or "rollback_unavailable" or "launch_blocked" => HttpStatusCode.Conflict,
            "storage_unavailable" or "io_error" or VirtualSystemDriveProblemCode.PackageStorageUnavailable => HttpStatusCode.ServiceUnavailable,
            "internal_error" => HttpStatusCode.InternalServerError,
            _ => HttpStatusCode.BadRequest,
        }, result, JsonOptions);

    private bool HasValidToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var expected = Encoding.UTF8.GetBytes(_mode.PairingToken);
        var actual = Encoding.UTF8.GetBytes(value);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object value,
        JsonSerializerOptions? options = null)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, options);
        response.ContentLength64 = payload.Length;
        try { await response.OutputStream.WriteAsync(payload); }
        catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException) { }
        finally { response.Close(); }
    }
}
