using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Services;

/// <summary>
/// Loads the language files shipped beside the client. Built-in UI must resolve stable resource
/// keys through <see cref="Get(string,string)"/>; the service deliberately does not inspect or
/// rewrite an Avalonia visual tree.
/// </summary>
public sealed class LocalizationService : ObservableObject, ISystemLanguage
{
    private const string DefaultLanguage = "en-US";
    private readonly ShellSettings _settings;
    private readonly Dictionary<string, LanguageFile> _languages;
    private string _currentLanguage;

    public LocalizationService(ShellSettings settings)
    {
        _settings = settings;
        _languages = LoadLanguageFiles();
        _currentLanguage = ResolveLanguage(settings.Language);
        AvailableLanguages = _languages.Values
            .OrderBy(language => language.SortOrder)
            .Select(language => new SystemLanguageOption(language.Culture, language.DisplayName ?? language.Culture))
            .ToArray();

        _settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellSettings.Language))
                SetLanguage(_settings.Language);
        };
    }

    public string CurrentLanguage => _currentLanguage;
    public IReadOnlyList<SystemLanguageOption> AvailableLanguages { get; }
    public event EventHandler<SystemLanguageChangedEventArgs>? LanguageChanged;

    /// <summary>Resolves a stable resource key with an English source fallback.</summary>
    public string Get(string key, string englishFallback)
    {
        var language = _languages.GetValueOrDefault(_currentLanguage);
        if (language?.Strings is { } strings
            && strings.TryGetValue(key, out var localized)
            && !string.IsNullOrWhiteSpace(localized)
            && !string.Equals(localized, key, StringComparison.Ordinal))
            return localized;

        // English is the single source-of-truth key table. Optional locale packs may lag
        // behind it, but an untranslated string must never surface the resource key.
        if (_languages.GetValueOrDefault(DefaultLanguage)?.Strings is { } english
            && english.TryGetValue(key, out var englishValue)
            && !string.IsNullOrWhiteSpace(englishValue)
            && !string.Equals(englishValue, key, StringComparison.Ordinal))
            return englishValue;

        return englishFallback;
    }

    private void SetLanguage(string requestedLanguage)
    {
        var next = ResolveLanguage(requestedLanguage);
        if (string.Equals(next, _currentLanguage, StringComparison.OrdinalIgnoreCase)) return;

        var previous = _currentLanguage;
        _currentLanguage = next;
        OnPropertyChanged(nameof(CurrentLanguage));
        void ApplyOnUiThread()
        {
            LanguageChanged?.Invoke(this, new SystemLanguageChangedEventArgs(previous, next));
        }

        if (Dispatcher.UIThread.CheckAccess())
            ApplyOnUiThread();
        else
            Dispatcher.UIThread.Post(ApplyOnUiThread);
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
        var directory = Path.Combine(System.AppContext.BaseDirectory, "Localization");
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
                    // One malformed optional language pack must not prevent the desktop from starting.
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

/// <summary>A selectable UI language discovered from a language file.</summary>
public sealed record SystemLanguageOption(string Culture, string DisplayName);
