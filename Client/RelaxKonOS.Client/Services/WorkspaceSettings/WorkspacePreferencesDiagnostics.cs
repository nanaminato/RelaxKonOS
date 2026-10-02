using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Services.WorkspaceSettings;

public enum PreferencesSaveFailure { Unexpected, Network, Session, InvalidSettings, ReloadRequired, Server, Conflict }

/// <summary>Automatic, bounded diagnostics. Never records tokens, preference bodies or exception messages.</summary>
internal static class WorkspacePreferencesDiagnostics
{
    private static readonly object Gate = new();
    private const long MaximumBytes = 2 * 1024 * 1024;
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "logs");

    public static PreferencesSaveFailure Classify(Exception exception) => exception switch
    {
        RelaxKonOSAuthException { Status: 401 or 403 } => PreferencesSaveFailure.Session,
        RelaxKonOSAuthException { Status: 400 } => PreferencesSaveFailure.InvalidSettings,
        RelaxKonOSAuthException { Status: 409 } => PreferencesSaveFailure.Conflict,
        RelaxKonOSAuthException { Status: 404 or 428 } => PreferencesSaveFailure.ReloadRequired,
        RelaxKonOSAuthException { Status: >= 500 } => PreferencesSaveFailure.Server,
        HttpRequestException or OperationCanceledException => PreferencesSaveFailure.Network,
        _ => PreferencesSaveFailure.Unexpected,
    };

    public static void Record(string operation, Guid workspaceId, long? revision, Exception exception, Guid? correlationId)
    {
        try
        {
            // Use closed, locally generated fields. Server response text and exception messages
            // can contain user data and are deliberately excluded.
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                operation,
                workspaceId,
                revision,
                failure = Classify(exception).ToString(),
                exceptionType = exception.GetType().Name,
                httpStatus = (exception as RelaxKonOSAuthException)?.Status,
                problemCode = exception is RelaxKonOSAuthException { Title: "settings.invalid_preferences" or "settings.invalid_revision"
                    or "settings.revision_required" or "settings.revision_conflict" } problem ? problem.Title : null,
                correlationId,
            });
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, $"workspace-preferences-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, entry + Environment.NewLine);
                foreach (var old in Directory.EnumerateFiles(DirectoryPath, "workspace-preferences-*.jsonl*"))
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7)) File.Delete(old);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }
}
