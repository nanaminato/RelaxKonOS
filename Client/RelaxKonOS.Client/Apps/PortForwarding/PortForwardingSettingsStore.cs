using System.Text.Json;

namespace RelaxKonOS.Client.Apps.PortForwarding;

/// <summary>Small, device-local settings file. No credentials, keys, or active forwards are written.</summary>
public sealed class PortForwardingSettingsStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string SettingsPath = Path.GetFullPath(settingsPath);

    public PortForwardingSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<PortForwardingSettings>(File.ReadAllText(SettingsPath))?.Normalize()
                ?? new PortForwardingSettings();
        }
        catch (IOException) { return new PortForwardingSettings(); }
        catch (UnauthorizedAccessException) { return new PortForwardingSettings(); }
        catch (JsonException) { return new PortForwardingSettings(); }
    }

    public void Save(PortForwardingSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
