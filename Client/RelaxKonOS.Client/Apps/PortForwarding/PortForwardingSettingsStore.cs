using System.Text.Json;

namespace RelaxKonOS.Client.Apps.PortForwarding;

/// <summary>Small, device-local settings file. No credentials, keys, or active forwards are written.</summary>
public sealed class PortForwardingSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "port-forwarding.json");

    public PortForwardingSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<PortForwardingSettings>(File.ReadAllText(SettingsPath))?.Normalize()
                ?? new PortForwardingSettings();
        }
        catch (IOException) { return new PortForwardingSettings(); }
        catch (JsonException) { return new PortForwardingSettings(); }
    }

    public void Save(PortForwardingSettings settings)
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
            // The active process remains valid when persistence is unavailable.
        }
    }
}
