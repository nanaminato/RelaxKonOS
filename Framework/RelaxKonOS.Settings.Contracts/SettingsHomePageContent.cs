using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.App.Settings;

public sealed class SettingsHomePageContent(
    IReadOnlyList<SettingsPageModel> pages,
    Func<IReadOnlyList<string>> pinnedRoutes,
    Func<string, string> labels) : ObservableObject
{
    public Func<string, string> Labels { get; } = labels;

    public IReadOnlyList<SettingsPageModel> Entries
    {
        get
        {
            var pinned = pinnedRoutes();
            return pages.Where(page =>
                (page.Route is "personalization" or "network" or "default-apps" or "time-language" or "account-security" or "system")
                && !pinned.Contains(page.Route)).ToArray();
        }
    }

    public bool HasEntries => Entries.Count > 0;

    public IReadOnlyList<SettingsPageModel> PinnedEntries => pinnedRoutes()
        .Select(route => pages.FirstOrDefault(page => page.Route == route)).OfType<SettingsPageModel>().ToArray();

    public bool HasPinnedEntries => PinnedEntries.Count > 0;

    public void RefreshPinnedRoutes()
    {
        OnPropertyChanged(nameof(PinnedEntries));
        OnPropertyChanged(nameof(HasPinnedEntries));
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(HasEntries));
    }

    public void RefreshLanguage() => OnPropertyChanged(nameof(Labels));
}
