using System.Text.Json;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

/// <summary>
/// Stores bounded, sanitized Server-side diagnostics so a failed runtime installation remains
/// diagnosable even when Mihomo has not started and its controller log is unavailable.
/// </summary>
public interface IProxyDiagnosticLogStore
{
    Task WriteAsync(string level, string message, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProxyLogEntryDto>> ReadAsync(int limit, CancellationToken cancellationToken);
}

public sealed class ProxyDiagnosticLogStore(IProxyPlatformPaths paths) : IProxyDiagnosticLogStore
{
    private const int MaximumEntries = 500;
    private const int MaximumMessageLength = 1_000;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(string level, string message, CancellationToken cancellationToken)
    {
        var entry = new ProxyLogEntryDto(DateTimeOffset.UtcNow, NormalizeLevel(level),
            ProxyLogSanitizer.Sanitize(message, MaximumMessageLength));
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var directory = paths.GetSanitizedLogDirectory();
                Directory.CreateDirectory(directory);
                var path = LogPath();
                await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine, cancellationToken);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                var lines = await File.ReadAllLinesAsync(path, cancellationToken);
                if (lines.Length > MaximumEntries)
                    await File.WriteAllLinesAsync(path, lines[^MaximumEntries..], cancellationToken);
            }
            finally { _gate.Release(); }
        }
        // Diagnostics must never turn an otherwise recoverable install failure into a new failure.
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<IReadOnlyList<ProxyLogEntryDto>> ReadAsync(int limit, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var path = LogPath();
                if (!File.Exists(path)) return [];
                var entries = new List<ProxyLogEntryDto>();
                foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<ProxyLogEntryDto>(line);
                        if (entry is not null) entries.Add(entry with { Message = ProxyLogSanitizer.Sanitize(entry.Message, MaximumMessageLength) });
                    }
                    catch (JsonException) { }
                }
                return entries.OrderByDescending(entry => entry.Timestamp).Take(Math.Clamp(limit, 1, MaximumEntries)).ToArray();
            }
            finally { _gate.Release(); }
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private string LogPath() => Path.Combine(paths.GetSanitizedLogDirectory(), "proxy-diagnostics.jsonl");
    private static string NormalizeLevel(string level) => level.Equals("error", StringComparison.OrdinalIgnoreCase) ? "error"
        : level.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "info";
}
