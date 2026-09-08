using System.Text.Json;

namespace RelaxKonOS.Examples.ServerMonitor.Services;

/// <summary>Locally persisted preferences for the Server Monitor package.</summary>
public sealed record MonitorSettings(int RefreshIntervalMilliseconds = 1000, int HistoryLength = 60)
{
    public MonitorSettings Normalize() => this with
    {
        RefreshIntervalMilliseconds = Math.Clamp(RefreshIntervalMilliseconds, 1000, 60000),
        HistoryLength = Math.Clamp(HistoryLength, 30, 240),
    };
}

public sealed class MonitorSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "external-app-settings", "server-monitor.json");

    public MonitorSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(SettingsPath))?.Normalize()
                ?? new MonitorSettings();
        }
        catch (IOException) { return new MonitorSettings(); }
        catch (JsonException) { return new MonitorSettings(); }
    }

    public void Save(MonitorSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (IOException)
        {
            // A failure to save preferences must not stop monitoring.
        }
    }
}
