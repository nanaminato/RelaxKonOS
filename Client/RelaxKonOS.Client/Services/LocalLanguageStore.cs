using System.Text.Json;

namespace RelaxKonOS.Client.Services;

/// <summary>Stores the display language used exclusively by the sign-in experience.</summary>
public sealed class LocalLanguageStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "client-language.json");

    public string Load()
    {
        try
        {
            return JsonSerializer.Deserialize<LocalLanguagePreference>(File.ReadAllText(_path))?.Language
                ?? "en-US";
        }
        catch (IOException) { return "en-US"; }
        catch (UnauthorizedAccessException) { return "en-US"; }
        catch (JsonException) { return "en-US"; }
    }

    public void Save(string language)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new LocalLanguagePreference(language)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record LocalLanguagePreference(string Language);
}
