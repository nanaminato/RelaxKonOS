using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.WebServers;

/// <summary>Resources used by Web Server Manager and Component Center.</summary>
public static class WebServerLocalization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Load("en-US"));
    private static readonly Lazy<Dictionary<string, string>> Chinese = new(() => Load("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Load("ja-JP"));

    public static string Resolve(ISystemLanguage language, string key, string? fallback = null)
    {
        var labels = language.CurrentLanguage.Split('-', 2)[0].ToLowerInvariant() switch
        {
            "zh" => Chinese.Value,
            "ja" => Japanese.Value,
            _ => English.Value,
        };
        return labels.GetValueOrDefault(key) ?? English.Value.GetValueOrDefault(key) ?? fallback ?? key;
    }

    public static IEnumerable<string> ResourceNames =>
        typeof(WebServerLocalization).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("WebServers.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(WebServerLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing web server localization resource '{resourceName}'.");

    private static Dictionary<string, string> Load(string culture)
    {
        using var source = Open($"WebServers.Localization.{culture}.json");
        return JsonSerializer.Deserialize<LanguageFile>(source)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}

public readonly struct WebServerStatusText
{
    private readonly Func<string>? _resolve;

    internal WebServerStatusText(Func<string> resolve) => _resolve = resolve;

    public static WebServerStatusText Literal(string? value) => new(() => value ?? string.Empty);
    public string Resolve() => _resolve?.Invoke() ?? string.Empty;
    public override string ToString() => Resolve();
    public static implicit operator string(WebServerStatusText value) => value.Resolve();
    public static implicit operator WebServerStatusText(string? value) => Literal(value);
}
