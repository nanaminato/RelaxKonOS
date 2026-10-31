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
    public string Summary => operation.Action switch
    {
        TailscaleAction.ConfigurePreferences => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Text("tailscale.confirm_summary_preferences"), target, Text("tailscale.action.ConfigurePreferences")),
        TailscaleAction.Logout => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Text("tailscale.confirm_summary_logout"), target, Text("tailscale.action.Logout")),
        _ => string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("tailscale.confirm_summary"),
            target, Text("tailscale.action." + operation.Action), Flag(baseline.Configuration.AcceptDns),
            Flag(operation.Configuration?.AcceptDns ?? baseline.Configuration.AcceptDns), Flag(baseline.Configuration.AcceptRoutes),
            Flag(operation.Configuration?.AcceptRoutes ?? baseline.Configuration.AcceptRoutes)),
    };
    public string Risk => Text(operation.Action == TailscaleAction.Logout ? "tailscale.logout_risk" : "tailscale.network_risk");
    public bool HasDetails => operation.Action == TailscaleAction.ConfigurePreferences;
    public string Details
    {
        get
        {
            if (baseline.Preferences is not { } before || operation.Preferences is not { } after) return "";
            var lines = new List<string>();
            void Line(string key, string from, string to)
            {
                var next = to == from ? "" : " → " + to;
                lines.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("tailscale.preference.change"),
                    Text("tailscale.preference." + key), from) + next);
            }
            Line("dns", Flag(before.AcceptDns), Flag(after.AcceptDns));
            Line("routes", Flag(before.AcceptRoutes), Flag(after.AcceptRoutes));
            Line("incoming", Text(before.ShieldsUp ? "tailscale.blocked" : "tailscale.allowed"),
                Text(after.ShieldsUp ? "tailscale.blocked" : "tailscale.allowed"));
            Line("exit_node", before.ExitNode is { Length: > 0 } oldExit ? oldExit : Text("tailscale.exit_node.none"),
                after.ExitNode is { Length: > 0 } newExit ? newExit : Text("tailscale.exit_node.none"));
            Line("lan_access", Flag(before.ExitNodeAllowLanAccess), Flag(after.ExitNodeAllowLanAccess));
            Line("unattended", Flag(before.Unattended), Flag(after.Unattended));
            Line("auto_update", Flag(before.AutoUpdate), Flag(after.AutoUpdate));
            Line("advertise_exit_node", Flag(before.AdvertiseExitNode), Flag(after.AdvertiseExitNode));
            return string.Join("\n", lines);
        }
    }
    internal void Changed(object? sender, SystemLanguageChangedEventArgs e)
    { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Details)); OnPropertyChanged(nameof(Risk)); }
}
