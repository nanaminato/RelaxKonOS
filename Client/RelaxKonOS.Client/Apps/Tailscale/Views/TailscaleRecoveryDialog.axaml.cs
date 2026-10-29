using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Tailscale;
namespace RelaxKonOS.Client.Apps.Tailscale.Views;
public partial class TailscaleRecoveryDialog : UserControl
{
    private readonly Action<bool> _complete;
    public TailscaleRecoveryDialog(TailscaleRecoverySnapshot preview, string target, LocalizationService language, Action<bool> complete)
    {
        InitializeComponent(); _complete = complete;
        var model = new TailscaleRecoveryText(preview, target, language); DataContext = model;
        AttachedToVisualTree += (_, _) => language.LanguageChanged += model.Changed;
        DetachedFromVisualTree += (_, _) => language.LanguageChanged -= model.Changed;
    }
    private void Cancel(object? sender, RoutedEventArgs e) => _complete(false);
    private void Confirm(object? sender, RoutedEventArgs e) => _complete(true);
}
public sealed class TailscaleRecoveryText(TailscaleRecoverySnapshot preview, string target, LocalizationService language) : ObservableObject
{
    private string Text(string key) => language.Get(key, key);
    private string Flag(bool value) => Text(value ? "tailscale.enabled" : "tailscale.disabled");
    public string OperationId => preview.OperationId.ToString("D");
    public string Summary => string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("tailscale.recovery.confirm_summary"),
        target, Text(preview.Observation!.Connected ? "tailscale.connected" : "tailscale.disconnected"),
        Flag(preview.Observation.Configuration.AcceptDns), Flag(preview.Observation.Configuration.AcceptRoutes));
    internal void Changed(object? sender, SystemLanguageChangedEventArgs e) => OnPropertyChanged(nameof(Summary));
}
