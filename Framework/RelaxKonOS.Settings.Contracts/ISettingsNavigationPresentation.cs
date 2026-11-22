using System.ComponentModel;

namespace RelaxKonOS.App.Settings.Views;

public interface ISettingsNavigationPresentation : INotifyPropertyChanging, INotifyPropertyChanged
{
    SettingsPageModel? SelectedPage { get; }
    string CurrentNavigationRoute { get; }
    string SearchQuery { get; set; }
    Func<string, string> Labels { get; }
    bool HasSearch { get; }
    bool IsRestoringNavigation { get; }
    string SearchLocationStatus { get; set; }
    event Action<SettingsSearchEntry>? SearchResultOpened;
    event Action? NavigationContextReset;
}
