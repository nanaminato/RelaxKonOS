using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Notepad;

public sealed class NotepadText(ISystemLanguage language)
{
    private static readonly Dictionary<string, Dictionary<string, string>> Languages = Load();
    internal ISystemLanguage Language => language;

    public string this[string key]
    {
        get
        {
            var culture = language.CurrentLanguage;
            if (!Languages.TryGetValue(culture, out var strings))
            {
                var neutral = culture.Split('-', 2)[0];
                strings = Languages.FirstOrDefault(pair => pair.Key.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase)).Value;
            }
            return strings?.GetValueOrDefault(key) ?? Languages["en-US"][key];
        }
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(NotepadText).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("Notepad.Localization.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var document = JsonSerializer.Deserialize<LanguageFile>(stream)!;
            languages.Add(document.Culture, document.Strings);
        }
        return languages;
    }

    private sealed record LanguageFile(string Culture, Dictionary<string, string> Strings);
}
