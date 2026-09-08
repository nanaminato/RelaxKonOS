using System.Text.Json;

namespace RelaxKonOS.Client.Services;

/// <summary>Stores non-sensitive, device-local choices for sign-in notifications.</summary>
public sealed class LoginNotificationPreferenceStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "login-notifications.json");

    public bool IsPasswordSaveWarningDismissed()
    {
        try
        {
            return JsonSerializer.Deserialize<LoginNotificationPreferences>(File.ReadAllText(_path))?.SuppressPasswordSaveWarning
                ?? false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    public void DismissPasswordSaveWarning()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new LoginNotificationPreferences(true)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record LoginNotificationPreferences(bool SuppressPasswordSaveWarning);
}
