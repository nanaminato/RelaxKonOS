using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Tailscale;
namespace RelaxKonOS.Client.Apps.Tailscale.Views;
public partial class TailscaleConfirmationDialog : UserControl
{
    private readonly Action<bool> _complete;
    public TailscaleConfirmationDialog(TailscaleOperation operation, TailscaleControlSnapshot baseline, string target,
        LocalizationService language, Action<bool> complete)
    {
        InitializeComponent(); _complete = complete;
        var model = new TailscaleConfirmationText(operation, baseline, target, language); DataContext = model;
        AttachedToVisualTree += (_, _) => language.LanguageChanged += model.Changed;
        DetachedFromVisualTree += (_, _) => language.LanguageChanged -= model.Changed;
    }
    private void Cancel(object? sender, RoutedEventArgs e) => _complete(false);
    private void Confirm(object? sender, RoutedEventArgs e) => _complete(true);
}
public sealed class TailscaleConfirmationText(TailscaleOperation operation, TailscaleControlSnapshot baseline, string target,
    LocalizationService language) : ObservableObject
{
    public string OperationId => operation.OperationId.ToString("D");
    private string Text(string key) => language.Get(key, key);
    private string Flag(bool value) => Text(value ? "tailscale.enabled" : "tailscale.disabled");
    public string Summary => string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("tailscale.confirm_summary"),
        target, Text("tailscale.action." + operation.Action), Flag(baseline.Configuration.AcceptDns),
        Flag(operation.Configuration?.AcceptDns ?? baseline.Configuration.AcceptDns), Flag(baseline.Configuration.AcceptRoutes),
        Flag(operation.Configuration?.AcceptRoutes ?? baseline.Configuration.AcceptRoutes));
    internal void Changed(object? sender, SystemLanguageChangedEventArgs e) => OnPropertyChanged(nameof(Summary));
}
