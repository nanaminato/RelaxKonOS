using System.Text.Json;

namespace RelaxKonOS.App.ImageViewer;

internal static class ImageViewerText
{
    private const string Prefix = "ImageViewer.Localization.";
    private static readonly Dictionary<string, Dictionary<string, string>> Languages = Load();

    public static string Get(string culture, string key)
    {
        if (!Languages.TryGetValue(culture, out var strings))
        {
            var neutral = culture.Split('-', 2)[0];
            strings = Languages.FirstOrDefault(pair => pair.Key.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase)).Value;
        }

        return strings?.GetValueOrDefault(key) ?? Languages["en-US"][key];
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(ImageViewerText).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var document = JsonSerializer.Deserialize<LanguageFile>(stream)!;
            languages.Add(document.Culture, document.Strings);
        }
        return languages;
    }

    private sealed record LanguageFile(string Culture, Dictionary<string, string> Strings);
}
