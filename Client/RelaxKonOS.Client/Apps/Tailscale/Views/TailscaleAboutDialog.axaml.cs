using RelaxKonOS.AppSDK;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.Client.Apps.Tailscale.Views;

public partial class TailscaleAboutDialog : UserControl
{
    private readonly Action _complete;
    public TailscaleAboutDialog(TailscaleAboutText text, Action complete)
    {
        InitializeComponent();
        _complete = complete;
        DataContext = text;
        AttachedToVisualTree += (_, _) => text.Language.LanguageChanged += text.RefreshLanguage;
        DetachedFromVisualTree += (_, _) => text.Language.LanguageChanged -= text.RefreshLanguage;
    }
    private void Close(object? sender, RoutedEventArgs e) => _complete();
}

public sealed class TailscaleAboutText(TailscaleText language, string managerVersion,
    string host, string hostVersion, string deviceName, string tailnet, string account) : ObservableObject
{
    internal TailscaleText Language => language;
    public Func<string, string> Labels => language.Labels;
    internal void RefreshLanguage(object? sender, SystemLanguageChangedEventArgs args) => OnPropertyChanged(string.Empty);
    public string Summary => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        language.Get("tailscale.about.summary_format", ""), managerVersion, host, hostVersion, deviceName, tailnet, account);
}
