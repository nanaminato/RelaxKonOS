using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Services;

/// <summary>
/// Durable, privacy-safe trace for local URI application activation. Query strings and URI
/// fragments are intentionally never recorded because they can contain search terms or tokens.
/// </summary>
public sealed class UriSchemeRoutingDiagnostics : IAppActivationDiagnostics
{
    private static readonly object FileGate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "logs", "uri-scheme-routing.log");

    public static string FilePath => LogPath;

    public void Record(string message)
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
            // A diagnostic write must never break application activation.
        }
    }
}
