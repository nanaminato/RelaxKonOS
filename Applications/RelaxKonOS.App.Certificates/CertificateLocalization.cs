using System.Text.Json;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Certificates;

/// <summary>Certificate Manager resources shared with the desktop host's HTTPS settings.</summary>
public static class CertificateLocalization
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
        typeof(CertificateLocalization).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Certificates.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(CertificateLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing certificate localization resource '{resourceName}'.");

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = Open($"Certificates.Localization.{language}.json");
        return JsonSerializer.Deserialize<LanguageFile>(stream)!.Strings;
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}

public readonly struct CertificateStatusText
{
    private readonly Func<string>? _resolve;

    internal CertificateStatusText(Func<string> resolve) => _resolve = resolve;

    public string Resolve() => _resolve?.Invoke() ?? string.Empty;
    public override string ToString() => Resolve();
    public static implicit operator string(CertificateStatusText value) => value.Resolve();

    internal static CertificateStatusText Join(string separator, IEnumerable<CertificateStatusText> values)
    {
        var parts = values.ToArray();
        return new(() => string.Join(separator, parts.Select(part => part.Resolve())));
    }
}
