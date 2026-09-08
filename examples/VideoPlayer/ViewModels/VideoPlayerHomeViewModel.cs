using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Examples.VideoPlayer.Services;

namespace RelaxKonOS.Examples.VideoPlayer.ViewModels;

public sealed partial class VideoPlayerHomeViewModel : ObservableObject, IDisposable
{
    private readonly VideoPlayerLocalizer _localizer;

    public VideoPlayerHomeViewModel(VideoPlayerLocalizer localizer)
    {
        _localizer = localizer;
        _localizer.LanguageChanged += OnLanguageChanged;
        RefreshLocalizedText();
    }

    [ObservableProperty] private string _titleText = string.Empty;
    [ObservableProperty] private string _hintText = string.Empty;

    public void Dispose() => _localizer.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs args) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        TitleText = _localizer.Get("app.name", "Video Player");
        HintText = _localizer.Get("home.hint", "Open a supported video in File Explorer, then choose Open with > Video Player. The application requests only server.files.read and uses a short-lived host media lease for playback.");
    }
}
