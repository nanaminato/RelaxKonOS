using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Browser.ViewModels;

/// <summary>Each tab owns its navigation state; the view owns its native surface.</summary>
public partial class BrowserTabViewModel : LocalizedObservableObject
{
    [ObservableProperty] private Uri? _source;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    public string Title => Source is null ? LocalizedText.Get("browser.new_tab") : Source.IsAbsoluteUri ? Source.Host : Source.ToString();
    partial void OnSourceChanged(Uri? value) => OnPropertyChanged(nameof(Title));
}
