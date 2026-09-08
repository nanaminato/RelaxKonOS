using System.Diagnostics;
using Avalonia.Threading;

namespace RelaxKonOS.Client.Apps.Browser;

/// <summary>
/// Durable, privacy-conscious diagnostics for the platform WebView. Native WebView traffic
/// does not pass through the application's HttpClient diagnostics pipeline, so a separate
/// trace is required when investigating UI hangs during navigation.
/// </summary>
internal static class BrowserDiagnostics
{
    private static readonly object FileGate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "logs", "browser-diagnostics.log");

    private static long _lastUiPulse = Stopwatch.GetTimestamp();
    private static int _watchdogStarted;
    private static int _unresponsiveReported;

    public static string FilePath => LogPath;

    /// <summary>Starts a process-wide UI heartbeat and records stalls without blocking the UI thread.</summary>
    public static void EnsureUiWatchdog()
    {
        if (Interlocked.Exchange(ref _watchdogStarted, 1) != 0)
            return;

        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        heartbeat.Tick += (_, _) =>
        {
            Interlocked.Exchange(ref _lastUiPulse, Stopwatch.GetTimestamp());
            if (Interlocked.Exchange(ref _unresponsiveReported, 0) != 0)
                Record("UI heartbeat recovered.");
        };
        heartbeat.Start();

        _ = new Timer(_ =>
        {
            var stalledFor = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUiPulse));
            if (stalledFor >= TimeSpan.FromSeconds(2) && Interlocked.Exchange(ref _unresponsiveReported, 1) == 0)
                Record($"UI heartbeat stalled for at least {stalledFor.TotalMilliseconds:F0} ms.");
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Record($"Browser diagnostics started. Log file: {LogPath}");
    }

    public static void Record(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.UtcNow:O} [tid:{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Diagnostics must never make the browser or the client less reliable.
        }
    }

    public static string SanitizeUri(Uri? uri)
    {
        if (uri is null)
            return "<null>";

        if (!uri.IsAbsoluteUri)
            return "<relative-uri>";

        // Query strings can contain search terms, forwarding credentials, or tokens.
        return new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }
}
