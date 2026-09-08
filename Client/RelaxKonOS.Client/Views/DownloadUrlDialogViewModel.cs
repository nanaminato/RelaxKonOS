using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Views;

/// <summary>Displays a trusted package URL and delegates clipboard access to the hosting application.</summary>
public sealed partial class DownloadUrlDialogViewModel(string url, Func<string, Task> copyAsync, Action close) : ObservableObject
{
    public string Url { get; } = url;

    [ObservableProperty] private string _copyStatus = string.Empty;

    [RelayCommand]
    private async Task CopyAsync()
    {
        await copyAsync(Url);
        CopyStatus = LocalizedText.Get("download_url.copied");
    }

    [RelayCommand]
    private void Close() => close();
}
