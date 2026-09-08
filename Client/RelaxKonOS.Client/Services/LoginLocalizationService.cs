using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.Client.Services;

/// <summary>
/// Localizes the sign-in window using a machine-local preference. This service deliberately does
/// not read or modify workspace settings, so connecting and disconnecting cannot affect it.
/// </summary>
public sealed class LoginLocalizationService : ObservableObject
{
    private const string DefaultLanguage = "en-US";
    private readonly LocalLanguageStore _store;
    private readonly Dictionary<string, LanguageFile> _languages;
    private string _currentLanguage;

    public LoginLocalizationService(LocalLanguageStore store)
    {
        _store = store;
        _languages = LoadLanguageFiles();
        _currentLanguage = ResolveLanguage(store.Load());
        AvailableLanguages = _languages.Values
            .OrderBy(language => language.SortOrder)
            .Select(language => new SystemLanguageOption(language.Culture, language.DisplayName ?? language.Culture))
            .ToArray();
    }

    public string CurrentLanguage => _currentLanguage;
    public IReadOnlyList<SystemLanguageOption> AvailableLanguages { get; }
    public event EventHandler? LanguageChanged;

    public string Get(string key, string englishFallback)
    {
        if (_languages.GetValueOrDefault(_currentLanguage)?.Strings is { } strings
            && strings.TryGetValue(key, out var localized)
            && !string.IsNullOrWhiteSpace(localized)
            && !string.Equals(localized, key, StringComparison.Ordinal))
            return localized;

        if (_languages.GetValueOrDefault(DefaultLanguage)?.Strings is { } english
            && english.TryGetValue(key, out var englishValue)
            && !string.IsNullOrWhiteSpace(englishValue)
            && !string.Equals(englishValue, key, StringComparison.Ordinal))
            return englishValue;

        return englishFallback;
    }

    public void SetLanguage(string requestedLanguage)
    {
        var next = ResolveLanguage(requestedLanguage);
        if (string.Equals(next, _currentLanguage, StringComparison.OrdinalIgnoreCase)) return;

        _currentLanguage = next;
        _store.Save(next);
        OnPropertyChanged(nameof(CurrentLanguage));
        if (Dispatcher.UIThread.CheckAccess())
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        else
            Dispatcher.UIThread.Post(() => LanguageChanged?.Invoke(this, EventArgs.Empty));
    }

    private string ResolveLanguage(string requestedLanguage)
    {
        if (_languages.ContainsKey(requestedLanguage)) return requestedLanguage;
        var neutral = requestedLanguage.Split('-', 2)[0];
        return _languages.Keys.FirstOrDefault(language => language.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase))
            ?? (_languages.ContainsKey(DefaultLanguage) ? DefaultLanguage : _languages.Keys.First());
    }

    private static Dictionary<string, LanguageFile> LoadLanguageFiles()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Localization");
        var loaded = new Dictionary<string, LanguageFileBuilder>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var language = JsonSerializer.Deserialize<LanguageFile>(File.ReadAllText(path));
                    if (language is not { Culture.Length: > 0 }) continue;
                    if (!loaded.TryGetValue(language.Culture, out var merged))
                        loaded[language.Culture] = merged = new LanguageFileBuilder(language.Culture);
                    merged.Merge(language, path);
                }
                catch (JsonException)
                {
                    // A malformed optional language pack must not prevent sign-in.
                }
            }
        }

        if (loaded.Count == 0)
            return new Dictionary<string, LanguageFile>(StringComparer.OrdinalIgnoreCase)
            {
                [DefaultLanguage] = new LanguageFile(DefaultLanguage, "English", 0, new Dictionary<string, string>()),
            };
        return loaded.ToDictionary(pair => pair.Key, pair => pair.Value.Build(), StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LanguageFile(string Culture, string? DisplayName, int? SortOrder, Dictionary<string, string>? Strings);

    private sealed class LanguageFileBuilder(string culture)
    {
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
        private string? _displayName;
        private int? _sortOrder;

        public void Merge(LanguageFile fragment, string path)
        {
            _displayName ??= fragment.DisplayName;
            _sortOrder ??= fragment.SortOrder;
            foreach (var (key, value) in fragment.Strings ?? [])
            {
                if (!_strings.TryAdd(key, value))
                    throw new JsonException($"Duplicate localization key '{key}' in '{path}'.");
            }
        }

        public LanguageFile Build() => new(culture, _displayName ?? culture, _sortOrder ?? int.MaxValue, _strings);
    }
}
