using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Localization;

/// <summary>
/// Base class for view models whose exposed text follows the workspace display language.
///
/// The client resolves every resource through <see cref="LocalizationService.Get(string,string)"/>,
/// which always returns the string for the <em>current</em> language. The problem is that a view
/// model which caches a resolved string in a field (or in a constructor) freezes it: switching the
/// display language re-raises <see cref="ObservableObject.PropertyChanged"/>, but re-reading a field
/// returns the stale value unless the field is recomputed.
///
/// Deriving from this class subscribes to <see cref="LocalizationService.LanguageChanged"/> and
/// re-raises the change notification for every public property. Any property that derives its text
/// from <see cref="T"/> (or from <see cref="LocalizedText"/>) therefore re-localizes immediately.
/// Text that must be stored in a field should store neutral state (an enum, a key) rather than the
/// resolved string, and expose a computed property that resolves it through <see cref="T"/>.
/// </summary>
public abstract class LocalizedObservableObject : ObservableObject
{
    private readonly IDisposable? _languageSubscription;

    protected LocalizedObservableObject() : this(null)
    {
    }

    /// <summary>
    /// Creates the base with an explicit localization source. Pass the already-resolved service when
    /// the caller holds one; otherwise the shared container instance is used.
    /// </summary>
    protected LocalizedObservableObject(LocalizationService? localization)
    {
        var service = localization ?? TryResolveLocalization();
        if (service is null) return;

        // The language change is raised on the UI thread by LocalizationService, so a plain
        // handler is safe. Re-notifying with an empty property name refreshes every binding,
        // which is exactly what a live computed property needs.
        void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => RefreshLocalizedProperties();
        service.LanguageChanged += OnLanguageChanged;
        _languageSubscription = new LanguageSubscription(service, OnLanguageChanged);
    }

    /// <summary>Re-reads every localized property. Called when the display language changes.</summary>
    protected void RefreshLocalizedProperties() => OnPropertyChanged(string.Empty);

    /// <summary>Resolves a stable resource key with an English source fallback.</summary>
    protected static string T(string key, string englishFallback) => LocalizedText.Get(key, englishFallback);

    private static LocalizationService? TryResolveLocalization()
    {
        try
        {
            return App.Services.GetService<LocalizationService>();
        }
        catch
        {
            // Unit-test and design-time hosts do not initialize the service container.
            return null;
        }
    }

    private sealed class LanguageSubscription(LocalizationService service, EventHandler<SystemLanguageChangedEventArgs> handler) : IDisposable
    {
        public void Dispose() => service.LanguageChanged -= handler;
    }
}
