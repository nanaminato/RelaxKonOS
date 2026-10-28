using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.Modules;

/// <summary>
/// Read-only cache of the official module inventory the connected server reports.
///
/// <para>
/// Its only job is to turn "this capability is missing" into a reason the operator can act on: the
/// module that provides it is disabled, a host/platform boundary withholds it, or no module on that
/// build declares it at all. It therefore reads once per login, never blocks a launch, and treats
/// every failure as "no explanation available" instead of an error the user has to dismiss. Module
/// state itself is still changed only by server configuration plus a restart; nothing here writes.
/// </para>
/// </summary>
public interface IServerModuleCatalog
{
    /// <summary>Last successful inventory for the current login, or null when it was never read.</summary>
    IReadOnlyList<ServerModuleDto>? Cached { get; }

    /// <summary>Why the last read produced nothing, for diagnostics; null when it succeeded.</summary>
    string? LastFailure { get; }

    /// <summary>Reads the inventory once per login. Never throws: diagnostics must not gate a launch.</summary>
    Task<IReadOnlyList<ServerModuleDto>?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a background read when nothing is cached yet; returns immediately.</summary>
    void EnsureLoaded();

    /// <summary>Forgets the cached inventory, e.g. when the login ends.</summary>
    void Invalidate();
}

public sealed class ServerModuleCatalog : IServerModuleCatalog, IDisposable
{
    public const string HttpClientName = "server-modules";

    private readonly HttpClient _http;
    private readonly IAuthSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private IReadOnlyList<ServerModuleDto>? _cached;
    private string? _cachedLogin;
    private string? _lastFailure;
    private bool _disposed;

    public ServerModuleCatalog(HttpClient http, IAuthSession session)
    {
        _http = http;
        _session = session;
        _session.StateChanged += OnSessionChanged;
    }

    public IReadOnlyList<ServerModuleDto>? Cached
    {
        get
        {
            lock (_cacheLock)
                return _cachedLogin is not null && _cachedLogin == _session.ServiceId ? _cached : null;
        }
    }

    public string? LastFailure
    {
        get { lock (_cacheLock) return _lastFailure; }
    }

    public void EnsureLoaded()
    {
        if (Cached is not null || _session.State != AuthSessionState.Authenticated) return;
        // Fire-and-forget on purpose: the caller is a synchronous launch decision and must not wait
        // for a diagnostic request. GetAsync swallows its own failures, so nothing is unobserved.
        _ = GetAsync(CancellationToken.None);
    }

    public async Task<IReadOnlyList<ServerModuleDto>?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Cached is { } cached) return cached;
        if (_session.State != AuthSessionState.Authenticated || _session.EffectiveBaseUrl is not { } baseUrl)
            return null;

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            // Another caller may have completed the read while this one waited for the gate.
            if (Cached is { } completed) return completed;

            var serviceId = _session.ServiceId;
            var session = _session.CurrentSession;
            var uri = new Uri(new Uri(baseUrl, UriKind.Absolute), ServerApiRoutes.Modules.TrimStart('/'));
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RecordFailure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            var modules = await response.Content
                .ReadFromJsonAsync<List<ServerModuleDto>>(RelaxKonOSJsonOptions.Default, cancellationToken)
                .ConfigureAwait(false);
            if (modules is null)
            {
                RecordFailure("The server returned an empty module inventory.");
                return null;
            }

            // A read that outlived its login describes a different server; discarding it keeps the
            // cache from explaining the next server's apps with the previous one's modules.
            if (!ReferenceEquals(session, _session.CurrentSession) || serviceId != _session.ServiceId)
                return null;

            lock (_cacheLock)
            {
                _cached = modules;
                _cachedLogin = serviceId;
                _lastFailure = null;
            }
            return modules;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RecordFailure(exception.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _cached = null;
            _cachedLogin = null;
            _lastFailure = null;
        }
    }

    private void RecordFailure(string message)
    {
        lock (_cacheLock) _lastFailure = message;
    }

    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args)
    {
        Invalidate();
        if (args.State == AuthSessionState.Authenticated) EnsureLoaded();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.StateChanged -= OnSessionChanged;
        _gate.Dispose();
    }
}
