using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelaxKonOS.Client.Services;

/// <summary>
/// Stores the completion state of the desktop welcome flow on this device.
/// This is deliberately separate from workspace preferences so a transient server save failure
/// cannot make an already-dismissed welcome dialog appear on every later sign-in.
/// </summary>
public sealed class DesktopWelcomePreferenceStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "desktop-welcome.json");

    public bool HasCompleted(string? serverUrl, string? username)
    {
        var accountKey = CreateAccountKey(serverUrl, username);
        if (accountKey is null) return false;

        return LoadCompletedAccountKeys().Contains(accountKey, StringComparer.Ordinal);
    }

    public void MarkCompleted(string? serverUrl, string? username)
    {
        var accountKey = CreateAccountKey(serverUrl, username);
        if (accountKey is null) return;

        try
        {
            var completedAccountKeys = LoadCompletedAccountKeys();
            if (completedAccountKeys.Contains(accountKey, StringComparer.Ordinal)) return;

            completedAccountKeys.Add(accountKey);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new DesktopWelcomePreferences(completedAccountKeys)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private static string? CreateAccountKey(string? serverUrl, string? username)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(username)) return null;

        var identity = $"{serverUrl.TrimEnd('/').ToUpperInvariant()}\n{username}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private List<string> LoadCompletedAccountKeys()
    {
        try
        {
            return JsonSerializer.Deserialize<DesktopWelcomePreferences>(File.ReadAllText(_path))
                ?.CompletedAccountKeys ?? [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (JsonException) { return []; }
    }

    private sealed record DesktopWelcomePreferences(List<string>? CompletedAccountKeys);
}
