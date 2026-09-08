using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using RemoteOS.Shell;

namespace Example.Windows11DesktopShell.Services;

/// <summary>Loads package-owned language files and follows the host workspace language.</summary>
public sealed class ShellLocalizer : IDisposable
{
    private const string DefaultCulture = "en-US";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ILocalizationSnapshot _systemLanguage;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _languages;
    private string _culture;
    private bool _disposed;

    public ShellLocalizer(ILocalizationSnapshot systemLanguage)
    {
        _systemLanguage = systemLanguage;
        _languages = LoadLanguageFiles();
        _culture = ResolveCulture(systemLanguage.Language);
        _systemLanguage.LanguageChanged += OnSystemLanguageChanged;
    }

    public string Culture => _culture;
    public event EventHandler? LanguageChanged;

    public string Get(string key, string fallback)
    {
        if (_languages.GetValueOrDefault(_culture)?.TryGetValue(key, out var localized) == true
            && !string.IsNullOrWhiteSpace(localized)) return localized;
        if (_languages.GetValueOrDefault(DefaultCulture)?.TryGetValue(key, out var english) == true
            && !string.IsNullOrWhiteSpace(english)) return english;
        return fallback;
    }

    public CultureInfo GetCultureInfo()
    {
        try { return CultureInfo.GetCultureInfo(_culture); }
        catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo(DefaultCulture); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _systemLanguage.LanguageChanged -= OnSystemLanguageChanged;
    }

    private void OnSystemLanguageChanged(object? sender, ShellLanguageChangedEventArgs args)
    {
        var next = ResolveCulture(args.CurrentLanguage);
        if (string.Equals(next, _culture, StringComparison.OrdinalIgnoreCase)) return;
        void Apply()
        {
            if (_disposed) return;
            _culture = next;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private string ResolveCulture(string requested)
    {
        if (_languages.ContainsKey(requested)) return requested;
        var neutral = requested.Split('-', 2)[0];
        return _languages.Keys.FirstOrDefault(culture => culture.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase))
               ?? (_languages.ContainsKey(DefaultCulture) ? DefaultCulture : _languages.Keys.FirstOrDefault() ?? DefaultCulture);
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadLanguageFiles()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(Path.GetDirectoryName(typeof(ShellLocalizer).Assembly.Location) ?? string.Empty, "Localization");
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var file = JsonSerializer.Deserialize<LanguageFile>(File.ReadAllText(path), JsonOptions);
                if (file is { Culture.Length: > 0, Strings: not null }) result[file.Culture] = file.Strings;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return result;
    }

    private sealed record LanguageFile(string Culture, Dictionary<string, string>? Strings);
}
