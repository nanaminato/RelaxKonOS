using System.Diagnostics;
using System.Text.Json;

namespace RelaxKonOS.Client.Services.Diagnostics;

/// <summary>Bounded local interaction trace. Do not pass window titles, content or credentials.</summary>
internal static class TaskbarPreviewDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string Session = Guid.NewGuid().ToString("N")[..8];
    private const long MaximumBytes = 2 * 1024 * 1024;
    private static string? _path;
    private static long _sequence;

    public static string FilePath
    {
        get { lock (Gate) { Initialize(); return _path!; } }
    }

    // Tests select an isolated output directory before creating any shell controls.
    internal static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            if (_path is not null) return;
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RelaxKonOS", "logs");
            _path = Path.Combine(directory, $"taskbar-preview-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            try
            {
                Directory.CreateDirectory(directory);
                // Keep the current run and the five most recent previous runs (including rotations).
                var previous = Directory.EnumerateFiles(directory, "taskbar-preview-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Skip(5).ToArray();
                foreach (var old in previous)
                {
                    File.Delete(old);
                    if (File.Exists(old + ".previous")) File.Delete(old + ".previous");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Debug.WriteLine($"Taskbar preview trace initialization failed: {e.GetType().Name}");
            }
            var assembly = typeof(TaskbarPreviewDiagnostics).Assembly;
            Record("session.start", new
            {
                traceVersion = 2,
                executable = Environment.ProcessPath,
                clientAssembly = assembly.Location,
                buildId = assembly.ManifestModule.ModuleVersionId,
                platform = Environment.OSVersion.Platform.ToString(),
                logPath = _path,
            });
        }
    }

    public static void Record(string eventName, object? data = null)
    {
        try
        {
            lock (Gate)
            {
                Initialize();
                var line = JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.Now,
                    elapsedMs = Clock.ElapsedMilliseconds,
                    seq = ++_sequence,
                    session = Session,
                    pid = Environment.ProcessId,
                    thread = Environment.CurrentManagedThreadId,
                    @event = eventName,
                    data,
                });
                Directory.CreateDirectory(Path.GetDirectoryName(_path!)!);
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaximumBytes)
                    File.Move(_path, _path + ".previous", overwrite: true);
                File.AppendAllText(_path!, line + Environment.NewLine);
            }
        }
        catch (Exception e)
        {
            // Diagnostics must never interrupt pointer handling or window activation.
            Debug.WriteLine($"Taskbar preview trace write failed: {e.GetType().Name}");
        }
    }
}
