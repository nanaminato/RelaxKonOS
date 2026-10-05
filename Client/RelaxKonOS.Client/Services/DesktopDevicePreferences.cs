using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.Client.Services;

/// <summary>Non-sensitive device choices; never included in workspace preference payloads.</summary>
public sealed class DesktopDevicePreferences : ObservableObject
{
    public static readonly int[] ScaleChoices = [100, 125, 150, 175, 200];
    private readonly string _path;
    public DesktopDeviceOptions Value { get; private set; } = new();
    public bool SaveFailed { get; private set; }
    public event EventHandler? Changed;

    public DesktopDevicePreferences() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "desktop-device.json")) { }

    public DesktopDevicePreferences(string path)
    {
        _path = Path.GetFullPath(path);
        try { Value = Normalize(JsonSerializer.Deserialize<DesktopDeviceOptions>(File.ReadAllText(_path)) ?? new()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    public void Update(Func<DesktopDeviceOptions, DesktopDeviceOptions> edit)
    {
        var next = Normalize(edit(Value));
        if (next with { PinnedSettings = Value.PinnedSettings } == Value
            && next.PinnedSettings.SequenceEqual(Value.PinnedSettings)) return;
        Value = next;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(Value));
            File.Move(temporary, _path, overwrite: true);
            SaveFailed = false;
        }
        catch (IOException) { SaveFailed = true; }
        catch (UnauthorizedAccessException) { SaveFailed = true; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            OnPropertyChanged(string.Empty);
        }
    }

    private static DesktopDeviceOptions Normalize(DesktopDeviceOptions value) => value with
    {
        InterfaceScale = ScaleChoices.Contains(value.InterfaceScale) ? value.InterfaceScale : 100,
        PinnedSettings = (value.PinnedSettings ?? []).Where(PinnableRoutes.Contains).Distinct().ToArray()
    };

    public static readonly HashSet<string> PinnableRoutes = new(StringComparer.Ordinal)
    {
        "system", "network", "personalization", "personalization/colors", "personalization/style",
        "personalization/layout", "personalization/background", "apps", "default-apps", "account-security",
        "time-language", "developer", "accessibility", "system/preferences"
    };
}

public sealed record DesktopDeviceOptions
{
    public int InterfaceScale { get; init; } = 100;
    public bool ReducedMotion { get; init; }
    public bool HighContrast { get; init; }
    public bool NotificationsEnabled { get; init; } = true;
    public bool DoNotDisturb { get; init; }
    public bool RestoreTerminals { get; init; } = true;
    public bool KeepConnectionBarVisible { get; init; }
    public string[] PinnedSettings { get; init; } = [];
}
