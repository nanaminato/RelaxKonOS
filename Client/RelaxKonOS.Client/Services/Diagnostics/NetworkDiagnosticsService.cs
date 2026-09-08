using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;

namespace RelaxKonOS.Client.Services.Diagnostics;

/// <summary>
/// Host-owned in-memory recorder for RelaxKonOS API diagnostics. It stores completed HTTP
/// summaries and payloads in memory for the current client session.
/// </summary>
public sealed class NetworkDiagnosticsService : IDisposable
{
    public const int MaximumEntries = 500;
    public const int MaximumEstimatedBytes = 16 * 1024 * 1024;

    private readonly DeveloperModeService _developerMode;
    private readonly Func<IAuthSession> _sessionAccessor;
    private readonly object _gate = new();
    private readonly Queue<(NetworkDiagnosticEntry Entry, int Bytes)> _entries = new();
    private bool _recording;
    private int _estimatedBytes;
    private int _dropped;
    private long _nextId;
    private IAuthSession? _session;

    /// <remarks>
    /// <paramref name="sessionAccessor"/> must remain lazy: this service is created while the
    /// auth typed HttpClient pipeline is being assembled, and resolving <see cref="IAuthSession"/>
    /// there would create a circular dependency.
    /// </remarks>
    public NetworkDiagnosticsService(DeveloperModeService developerMode, Func<IAuthSession> sessionAccessor)
    {
        _developerMode = developerMode;
        _sessionAccessor = sessionAccessor;
        _developerMode.Changed += OnAvailabilityChanged;
    }

    public event EventHandler<NetworkDiagnosticsState>? StateChanged;
    public event EventHandler<NetworkDiagnosticEntry>? EntryCompleted;

    public NetworkDiagnosticsState State
    {
        get
        {
            lock (_gate)
                return GetStateLocked();
        }
    }

    internal bool IsRecording
    {
        get
        {
            lock (_gate)
                return _recording && IsAvailableLocked();
        }
    }

    public Task<NetworkDiagnosticsCommandResult> StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NetworkDiagnosticsState state;
        lock (_gate)
        {
            if (!IsAvailableLocked())
                return Task.FromResult(NetworkDiagnosticsCommandResult.Unavailable);
            _recording = true;
            state = GetStateLocked();
        }
        StateChanged?.Invoke(this, state);
        return Task.FromResult(NetworkDiagnosticsCommandResult.Succeeded);
    }

    public Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NetworkDiagnosticsState state;
        lock (_gate)
        {
            _recording = false;
            state = GetStateLocked();
        }
        StateChanged?.Invoke(this, state);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            ClearLocked();
        return Task.CompletedTask;
    }

    public NetworkDiagnosticsSnapshot GetSnapshot(NetworkDiagnosticsQuery? query = null)
    {
        lock (_gate)
        {
            var allEntries = _entries.Select(item => item.Entry).ToArray();
            var entries = allEntries.Where(entry => Matches(entry, query)).ToArray();
            return new NetworkDiagnosticsSnapshot(GetStateLocked(), entries, _dropped, allEntries.Length);
        }
    }

    internal bool ShouldCapture(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;
        if (uri.IsLoopback && uri.Port == DeveloperModeService.BridgePort)
            return true;
        var serverUrl = GetSession().ServerUrl;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
            return false;
        return Uri.Compare(uri, server, UriComponents.SchemeAndServer, UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    internal void Record(NetworkDiagnosticEntry entry)
    {
        NetworkDiagnosticEntry stored;
        lock (_gate)
        {
            if (!_recording || !IsAvailableLocked())
                return;
            stored = entry with { Id = ++_nextId };
            var size = EstimateSize(stored);
            while (_entries.Count > 0 && (_entries.Count >= MaximumEntries || _estimatedBytes + size > MaximumEstimatedBytes))
            {
                var removed = _entries.Dequeue();
                _estimatedBytes -= removed.Bytes;
                _dropped++;
            }
            if (size > MaximumEstimatedBytes)
            {
                _dropped++;
                return;
            }
            _entries.Enqueue((stored, size));
            _estimatedBytes += size;
        }
        EntryCompleted?.Invoke(this, stored);
    }

    internal static IReadOnlyDictionary<string, string> CaptureHeaders(params HttpHeaders?[] collections)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in collections)
        {
            if (collection is null)
                continue;
            foreach (var header in collection)
            {
                var value = string.Join(", ", header.Value);
                headers[header.Key] = headers.TryGetValue(header.Key, out var existing)
                    ? $"{existing}, {value}"
                    : value;
            }
        }
        return headers;
    }

    internal static async Task<NetworkDiagnosticPayload?> CapturePayloadAsync(HttpContent? content)
    {
        if (content is null)
            return null;

        try
        {
            var bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var mediaType = content.Headers.ContentType?.MediaType;
            if (!IsTextContent(mediaType))
                return new NetworkDiagnosticPayload(Convert.ToBase64String(bytes), $"base64 ({mediaType ?? "binary"})", bytes.Length);

            var encoding = GetEncoding(content.Headers.ContentType?.CharSet);
            return new NetworkDiagnosticPayload(encoding.GetString(bytes), $"{mediaType ?? "text/plain"}; charset={encoding.WebName}", bytes.Length);
        }
        catch (Exception exception)
        {
            return new NetworkDiagnosticPayload($"<Unable to capture body: {exception.Message}>", "unavailable", 0);
        }
    }

    internal static bool IsMediaContent(string? contentType, Uri? requestUri)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
             || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
             || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
             || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
            return true;
        var path = requestUri?.AbsolutePath ?? string.Empty;
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ErrorKind(Exception exception) => exception switch
    {
        TaskCanceledException => "Timeout",
        OperationCanceledException => "Cancelled",
        HttpRequestException => "Connection",
        _ => "Unexpected",
    };

    public void Dispose()
    {
        _developerMode.Changed -= OnAvailabilityChanged;
        if (_session is not null)
            _session.StateChanged -= OnSessionStateChanged;
    }

    private void OnAvailabilityChanged(object? sender, EventArgs args) => DisableAndClearIfUnavailable();

    private void OnSessionStateChanged(object? sender, AuthSessionStateChangedEventArgs args) => DisableAndClearIfUnavailable();

    private void DisableAndClearIfUnavailable()
    {
        NetworkDiagnosticsState state;
        lock (_gate)
        {
            if (IsAvailableLocked())
            {
                state = GetStateLocked();
            }
            else
            {
                _recording = false;
                ClearLocked();
                state = GetStateLocked();
            }
        }
        StateChanged?.Invoke(this, state);
    }

    private bool IsAvailableLocked() => _developerMode.IsEnabled && GetSession().State == AuthSessionState.Authenticated;

    private NetworkDiagnosticsState GetStateLocked() => IsAvailableLocked()
        ? new NetworkDiagnosticsState(true, _recording)
        : new NetworkDiagnosticsState(false, false, !_developerMode.IsEnabled ? "Developer Mode is disabled."
            : "Sign in to use Network Inspector.");

    private IAuthSession GetSession()
    {
        if (_session is not null)
            return _session;

        var session = _sessionAccessor();
        _session = session;
        session.StateChanged += OnSessionStateChanged;
        return session;
    }

    private void ClearLocked()
    {
        _entries.Clear();
        _estimatedBytes = 0;
        _dropped = 0;
    }

    private static bool Matches(NetworkDiagnosticEntry entry, NetworkDiagnosticsQuery? query)
    {
        if (query is null) return true;
        if (query.Kind is { } kind && entry.Kind != kind) return false;
        if (query.IsMedia is { } media && entry.IsMedia != media) return false;
        if (query.FailuresOnly == true && entry.Outcome == NetworkDiagnosticOutcome.Succeeded) return false;
        return string.IsNullOrWhiteSpace(query.Text)
            || query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(term => MatchesFilterTerm(entry, term));
    }

    private static bool MatchesFilterTerm(NetworkDiagnosticEntry entry, string term)
    {
        if (term.StartsWith("method:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(entry.Method, term[7..], StringComparison.OrdinalIgnoreCase);
        if (term.StartsWith("status-code:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(entry.StatusCode?.ToString(), term[12..], StringComparison.OrdinalIgnoreCase);
        if (term.StartsWith("mime-type:", StringComparison.OrdinalIgnoreCase))
            return entry.ContentType?.Contains(term[10..], StringComparison.OrdinalIgnoreCase) == true;
        if (term.StartsWith("larger-than:", StringComparison.OrdinalIgnoreCase))
            return entry.DeclaredContentLength is long size && size > ParseByteSize(term[12..]);
        if (term.Equals("is:failed", StringComparison.OrdinalIgnoreCase))
            return entry.Outcome != NetworkDiagnosticOutcome.Succeeded;
        if (term.Equals("is:media", StringComparison.OrdinalIgnoreCase))
            return entry.IsMedia;

        return entry.Source.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.PathAndQuery.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.Method?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || entry.ContentType?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || entry.StatusCode?.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static long ParseByteSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return long.MaxValue;
        var suffix = char.ToLowerInvariant(text[^1]);
        var multiplier = suffix switch { 'k' => 1024L, 'm' => 1024L * 1024L, _ => 1L };
        var number = multiplier == 1L ? text : text[..^1];
        return long.TryParse(number, out var value) && value >= 0 ? value * multiplier : long.MaxValue;
    }

    private static int EstimateSize(NetworkDiagnosticEntry entry) => 256
        + entry.Source.Length * 2 + entry.Name.Length * 2 + entry.PathAndQuery.Length * 2
        + (entry.ContentType?.Length ?? 0) * 2 + (entry.ErrorKind?.Length ?? 0) * 2
        + (entry.RequestHeaders?.Sum(pair => pair.Key.Length + pair.Value.Length) ?? 0) * 2
        + (entry.ResponseHeaders?.Sum(pair => pair.Key.Length + pair.Value.Length) ?? 0) * 2
        + (entry.RequestBody?.Content.Length ?? 0) * 2
        + (entry.ResponseBody?.Content.Length ?? 0) * 2;

    private static bool IsTextContent(string? mediaType) => string.IsNullOrEmpty(mediaType)
        || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);

    private static Encoding GetEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset); }
            catch (ArgumentException) { }
        }
        return Encoding.UTF8;
    }

}
