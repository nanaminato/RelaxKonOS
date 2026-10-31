using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
namespace RelaxKonOS.Client.Apps.Tailscale.Views;
public partial class TailscaleLoginDialog : UserControl
{
    private readonly Action<bool> _complete;
    public TailscaleLoginDialog(string target, Guid id, bool close, string observation, LocalizationService language, Action<bool> complete)
    {
        InitializeComponent(); _complete = complete;
        var model = new TailscaleLoginDialogText(target, id, close, observation, language); DataContext = model;
        AttachedToVisualTree += (_, _) => language.LanguageChanged += model.Changed;
        DetachedFromVisualTree += (_, _) => language.LanguageChanged -= model.Changed;
    }
    private void Cancel(object? sender, RoutedEventArgs e) => _complete(false);
    private void Confirm(object? sender, RoutedEventArgs e) => _complete(true);
}
public sealed class TailscaleLoginDialogText(string target, Guid id, bool close, string observation, LocalizationService language) : ObservableObject
{
    public string Target => target;
    public string OperationId => id.ToString("D");
    public string Observation => observation.Length == 0 ? "" : language.Get("tailscale.backend." + observation, language.Get("tailscale.backend_unknown", ""));
    public string Risk => language.Get(close ? "tailscale.login.close_risk" : "tailscale.login.start_risk", "");
    public string Decision => language.Get(close ? "tailscale.recovery.confirm_close" : "tailscale.login.start_and_open", "");
    internal void Changed(object? sender, SystemLanguageChangedEventArgs e) { OnPropertyChanged(nameof(Risk)); OnPropertyChanged(nameof(Decision)); OnPropertyChanged(nameof(Observation)); }
}
