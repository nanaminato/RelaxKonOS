using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Tailscale.Views;

public partial class TailscaleAboutDialog : UserControl
{
    private readonly Action _complete;
    public TailscaleAboutDialog(TailscaleAboutText text, Action complete)
    {
        InitializeComponent();
        _complete = complete;
        DataContext = text;
    }
    private void Close(object? sender, RoutedEventArgs e) => _complete();
}

public sealed class TailscaleAboutText(RelaxKonOS.Client.Services.LocalizationService language, string managerVersion,
    string host, string hostVersion, string deviceName, string tailnet, string account) : ObservableObject
{
    public string Summary => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        language.Get("tailscale.about.summary_format", ""), managerVersion, host, hostVersion, deviceName, tailnet, account);
}
