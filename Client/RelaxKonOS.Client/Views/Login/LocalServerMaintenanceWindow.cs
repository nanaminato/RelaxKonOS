using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Client.Services.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;

namespace RelaxKonOS.Client.Views.Login;

/// <summary>Owns review, execution and completion for a single local lifecycle action.</summary>
public sealed class LocalServerMaintenanceWindow : Window
{
    private readonly LocalWindowsServerInstaller _installer;
    private readonly LoginLocalizationService _localization;
    private readonly ServerDeploymentKind _kind;
    private StackPanel _review = null!;
    private Button _confirm = null!;
    private Button _close = null!;
    private TextBlock _status = null!;
    private bool _busy;
    private bool _finished;
    private string _message = string.Empty;
    public ServerHostSnapshotDto? Snapshot { get; private set; }
    public string Message => _message;
    private string T(string key, string fallback) => _localization.Get("login.local_manage_" + key, fallback);

    public LocalServerMaintenanceWindow(ServerDeploymentKind kind, ServerHostSnapshotDto reviewed,
        LocalWindowsServerInstaller installer, LoginLocalizationService localization)
    {
        if (kind is not (ServerDeploymentKind.Repair or ServerDeploymentKind.Configure or ServerDeploymentKind.Rollback or ServerDeploymentKind.Uninstall))
            throw new ArgumentOutOfRangeException(nameof(kind));
        _kind = kind;
        Snapshot = reviewed;
        _installer = installer;
        _localization = localization;
        Title = kind == ServerDeploymentKind.Configure ? T("settings", "Installation settings / components") : kind == ServerDeploymentKind.Repair ? T("repair", "Repair") :
            kind == ServerDeploymentKind.Rollback ? T("rollback", "Restore previous version") : T("uninstall", "Uninstall");
        Width = 640;
        Height = 620;
        MinWidth = 380;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildReview();
        Closing += (_, args) => args.Cancel = _busy;
    }

    private void BuildReview()
    {
        var reviewed = Snapshot!;
        var kind = _kind;
        var panel = _review = new StackPanel { Spacing = 16 };
        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(LocalManagementLayout.Text(T("version", "Version") + ": " + reviewed.Version, 16));
        if (kind == ServerDeploymentKind.Rollback)
            details.Children.Add(LocalManagementLayout.Text(T("previous", "Previous version") + ": " + reviewed.PreviousVersion, 16));
        details.Children.Add(LocalManagementLayout.Text(T("program_directory", "Program directory") + "\n" + reviewed.InstallRoot, 13, true));
        details.Children.Add(LocalManagementLayout.Text(T("data_directory", "Data directory") + "\n" + reviewed.DataRoot, 13, true));
        panel.Children.Add(LocalManagementLayout.Card(details));
        panel.Children.Add(LocalManagementLayout.Text(kind == ServerDeploymentKind.Uninstall ? T("uninstall_hint", "Remove Server; keep data by default") :
            T("review", "Review this local installation. The action may interrupt its services."), 13, true));
        var removeData = new CheckBox { Content = LocalManagementLayout.Text(T("delete", "Also permanently delete managed data")), IsVisible = kind == ServerDeploymentKind.Uninstall };
        var confirmDelete = new CheckBox { Content = LocalManagementLayout.Text(T("confirm_delete", "I confirm permanent deletion of the managed data shown above")), IsVisible = false };
        removeData.IsCheckedChanged += (_, _) => { confirmDelete.IsVisible = removeData.IsChecked == true; confirmDelete.IsChecked = false; };
        panel.Children.Add(removeData);
        panel.Children.Add(confirmDelete);
        var repairPackage = new TextBox { IsVisible = kind == ServerDeploymentKind.Repair, PlaceholderText = _localization.Get("server_center.repair_package_local", "Local ZIP of the installed version (optional)") };
        panel.Children.Add(repairPackage);
        var regenerate = new CheckBox { Content = LocalManagementLayout.Text(T("regenerate_certificate", "Regenerate a self-signed TLS certificate")), IsVisible = kind == ServerDeploymentKind.Repair };
        var identities = new TextBox { Text = "localhost,127.0.0.1", IsVisible = false };
        var certificateNote = new TextBlock { Text = T("certificate_note", "Enter certificate DNS names or IP addresses, separated by commas. Regeneration enables HTTPS and changes the certificate fingerprint; clients must review trust again."), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        regenerate.IsCheckedChanged += (_, _) => identities.IsVisible = certificateNote.IsVisible = regenerate.IsChecked == true;
        var firewall = new CheckBox { Content = LocalManagementLayout.Text(T("repair_firewall", "Check and repair the Server TCP firewall rule")), IsVisible = kind == ServerDeploymentKind.Repair && reviewed.Mode == ServerInstallMode.WindowsSystem };
        var certificateHeader = LocalManagementLayout.Header(T("certificate_options", "TLS certificate"), T("certificate_keep", "Keep the existing certificate unless you choose regeneration."), "certificates");
        certificateHeader.IsVisible = kind == ServerDeploymentKind.Repair;
        panel.Children.Add(certificateHeader);
        panel.Children.Add(regenerate);
        panel.Children.Add(certificateNote);
        panel.Children.Add(identities);
        var firewallHeader = LocalManagementLayout.Header(T("firewall_options", "Firewall"), string.Empty, "firewall");
        firewallHeader.IsVisible = kind == ServerDeploymentKind.Repair;
        panel.Children.Add(firewallHeader);
        panel.Children.Add(firewall);
        panel.Children.Add(new TextBlock { Text = T("firewall_note", "Only network listeners need an inbound rule. Loopback listeners are skipped. Rules are repaired only on active networks with the firewall enabled; the firewall itself is never enabled automatically."), TextWrapping = TextWrapping.Wrap, IsVisible = kind == ServerDeploymentKind.Repair });
        var endpoint = Uri.TryCreate(reviewed.ListenUrl, UriKind.Absolute, out var observedEndpoint) ? observedEndpoint : new Uri("http://127.0.0.1:5000");
        var port = new TextBox { Text = endpoint.Port.ToString(), IsVisible = kind == ServerDeploymentKind.Configure };
        var lan = new CheckBox { Content = T("lan", "Allow LAN access"), IsChecked = endpoint.Host == "0.0.0.0", IsVisible = kind == ServerDeploymentKind.Configure };
        var guardian = new CheckBox { Content = T("guardian", "Install process guardian"), IsChecked = reviewed.GuardianEnabled == true, IsVisible = kind == ServerDeploymentKind.Configure && reviewed.Mode == ServerInstallMode.WindowsSystem };
        var autoStart = new CheckBox { Content = _localization.Get("server_center.auto_start", "Start automatically"), IsChecked = reviewed.AutoStart == true, IsVisible = kind == ServerDeploymentKind.Configure && reviewed.AutoStart != null };
        var scopes = new[] {
            new FileAccessOption(ServerFileAccessScope.Restricted, _localization.Get("server_center.wizard.file_access_restricted", "RelaxKonOS data only")),
            new FileAccessOption(ServerFileAccessScope.Whitelist, _localization.Get("server_center.wizard.file_access_whitelist", "Selected directories")),
            new FileAccessOption(ServerFileAccessScope.Full, _localization.Get("server_center.wizard.file_access_full", "All local files")) };
        var access = new ComboBox { ItemsSource = scopes, ItemTemplate = new FuncDataTemplate<FileAccessOption>((option, _) => new TextBlock { Text = option?.Label }), SelectedItem = scopes.FirstOrDefault(option => option.Scope == reviewed.FileAccess), IsVisible = kind == ServerDeploymentKind.Configure && reviewed.FileAccess != null };
        var roots = new TextBox { Text = string.Join("\n", reviewed.FileRoots ?? []), AcceptsReturn = true, MinHeight = 80, IsVisible = access.IsVisible };
        bool SettingsChanged() => port.Text != endpoint.Port.ToString() || lan.IsChecked != (endpoint.Host == "0.0.0.0") || guardian.IsChecked != (reviewed.GuardianEnabled == true) || autoStart.IsChecked != (reviewed.AutoStart == true) || (access.SelectedItem as FileAccessOption)?.Scope != reviewed.FileAccess || roots.Text != string.Join("\n", reviewed.FileRoots ?? []);
        if (kind == ServerDeploymentKind.Configure) {
            var addRules = new Button { Content = _localization.Get("server_center.ensure_firewall", "Add rule for current listener") };
            addRules.Click += (_, _) => {
                if (SettingsChanged()) { _status.Text = _localization.Get("server_center.settings_first", "Apply or discard settings first."); return; }
                _ = RunAsync(async () => Snapshot = await _installer.MaintainAsync(ServerDeploymentKind.Firewall, reviewed, _localization.CurrentLanguage, true, removeFirewallRules: false));
            };
            panel.Children.Add(addRules);
            var removeRules = new Button { Content = _localization.Get("server_center.remove_firewall", "Remove managed Server firewall rules") };
            removeRules.Click += (_, _) => {
                if (SettingsChanged()) { _status.Text = _localization.Get("server_center.settings_first", "Apply or discard settings first."); return; }
                _ = RunAsync(async () => Snapshot = await _installer.MaintainAsync(ServerDeploymentKind.Firewall, reviewed, _localization.CurrentLanguage, true));
            };
            panel.Children.Add(removeRules);
        }
        roots.IsVisible = access.IsVisible && (access.SelectedItem as FileAccessOption)?.Scope == ServerFileAccessScope.Whitelist;
        access.SelectionChanged += (_, _) => roots.IsVisible = access.IsVisible && (access.SelectedItem as FileAccessOption)?.Scope == ServerFileAccessScope.Whitelist;
        panel.Children.Add(autoStart); panel.Children.Add(access); panel.Children.Add(roots);
        panel.Children.Add(port); panel.Children.Add(lan); panel.Children.Add(guardian);
        if (kind == ServerDeploymentKind.Configure)
            _ = new NativeDraftCloseGuard(this, () => _busy, () => !_finished &&
                (port.Text != endpoint.Port.ToString() || lan.IsChecked != (endpoint.Host == "0.0.0.0") || guardian.IsChecked != (reviewed.GuardianEnabled == true) || autoStart.IsChecked != (reviewed.AutoStart == true) || (access.SelectedItem as FileAccessOption)?.Scope != reviewed.FileAccess || roots.Text != string.Join("\n", reviewed.FileRoots ?? [])),
                async () =>
                {
                    var prompt = new Window { Title = _localization.Get("common.discard_title", "Discard changes?"), Width = 480, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    prompt.Content = new ConfirmDialogView { DataContext = new ConfirmDialogViewModel(
                        _localization.Get("common.discard_message", "Discard unsaved changes and close this editor?"),
                        accepted => prompt.Close(accepted), _localization.Get("common.discard", "Discard"), _localization.Get("common.cancel", "Cancel")) };
                    return await prompt.ShowDialog<bool>(this);
                }, () => { });
        if (kind == ServerDeploymentKind.Configure) panel.Children.Add(LocalManagementLayout.Text(T("settings_note", "Services restart. Removing Guardian stops its workloads and retains definitions and logs. TLS identity is preserved."), 13, true));
        var confirm = _confirm = new Button { Content = T("confirm", "Confirm and request administrator permission") };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        confirm.Click += (_, _) =>
        {
            if (removeData.IsChecked == true && confirmDelete.IsChecked != true)
            {
                error.Text = T("confirm_delete", "I confirm permanent deletion of the managed data shown above");
                return;
            }
            if (regenerate.IsChecked == true && string.IsNullOrWhiteSpace(identities.Text))
            {
                error.Text = T("certificate_names_required", "Enter certificate DNS names or IP addresses.");
                return;
            }
            if (kind == ServerDeploymentKind.Configure)
            {
                if (!int.TryParse(port.Text, out var configuredPort) || configuredPort is < 1 or > 65535) { error.Text = T("invalid_port", "Enter a port from 1 to 65535."); return; }
                _ = RunAsync(async () => Snapshot = await _installer.ConfigureAsync(reviewed,
                    _localization.CurrentLanguage, configuredPort, lan.IsChecked == true, guardian.IsChecked == true,
                    autoStart: reviewed.AutoStart != null ? autoStart.IsChecked : null, fileAccess: (access.SelectedItem as FileAccessOption)?.Scope,
                    fileRoots: roots.Text?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));
                return;
            }
            _ = RunAsync(async () => Snapshot = await _installer.MaintainAsync(kind, reviewed,
                _localization.CurrentLanguage, true, removeData.IsChecked == true ? ServerDataRetention.Delete : ServerDataRetention.Retain,
                regenerate.IsChecked == true, identities.Text, firewall.IsChecked == true, repairPackagePath: repairPackage.Text), firewall.IsChecked == true);
        };
        confirm.Classes.Add("accent");
        confirm.Padding = new Thickness(16, 10);
        confirm.Content = LocalManagementLayout.Text(kind == ServerDeploymentKind.Configure ? T("apply_settings", "Apply and verify") : kind == ServerDeploymentKind.Repair ? T("repair", "Repair") : kind == ServerDeploymentKind.Rollback ? T("rollback", "Restore previous version") : T("uninstall", "Uninstall"));
        ToolTip.SetTip(confirm, T("confirm", "Confirm and request administrator permission"));
        Avalonia.Automation.AutomationProperties.SetName(confirm, Title);
        panel.Children.Add(error);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap };

        var cancel = _close = new Button { Content = T("cancel", "Cancel") };
        cancel.Click += (_, _) => Close();
        var buttons = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        confirm.Margin = new Thickness(0, 0, 12, 8);
        cancel.Margin = new Thickness(0, 0, 0, 8);
        cancel.Padding = new Thickness(16, 10);
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        var footer = new StackPanel { Spacing = 10 };
        footer.Children.Add(_status);
        footer.Children.Add(buttons);
        Content = LocalManagementLayout.Shell(LocalManagementLayout.Header(Title!, T("maintenance_subtitle", "Review your options, then approve Windows administrator permission."),
            kind == ServerDeploymentKind.Repair ? "diagnostics" : kind == ServerDeploymentKind.Rollback ? "operations" : "host_settings"), panel, footer);
    }

    private async Task RunAsync(Func<Task> action, bool reportFirewall = false)
    {
        if (_busy) return;
        _busy = true;
        _message = T("working", "Waiting for administrator permission or processing the local operation…");
        _review.IsEnabled = false;
        _confirm.IsEnabled = _close.IsEnabled = false;
        _status.Text = _message;
        try
        {
            await action();
            _finished = true;
            _message = T("completed", "Local operation completed and status verified.");
            if (reportFirewall) _message += "\n" + (_installer.LastFirewallStatus switch
            {
                "ruleAdded" => T("firewall_repaired", "Server TCP firewall rule repaired."),
                "notApplicable" => T("firewall_loopback", "Loopback listener: no inbound firewall rule is needed."),
                "disabled" => T("firewall_disabled", "Firewall is disabled on active networks; no rule was added."),
                _ => T("firewall_unverified", "The receipt does not confirm a firewall rule repair.")
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            _message = T("cancelled", "Administrator permission was cancelled.");
        }
        catch (Exception error)
        {
            Snapshot = null;
            _message = error is LocalWindowsDeploymentFailedException failure ? failure.Receipt.SafeMessage ?? T("failed", "Local operation failed. Refresh status before retrying.") :
                T("failed", "Local operation failed. Refresh status before retrying.") + " (" + error.GetType().Name + ")";
            if (_installer.LastOperationId is { } id) _message += "\n" + T("operation", "Operation ID") + ": " + id;
        }
        finally
        {
            _busy = false;
            _review.IsEnabled = !_finished && Snapshot is not null;
            _close.IsEnabled = true;
            _confirm.IsEnabled = !_finished && Snapshot is not null;
            _status.Text = _message;
            _close.Content = T("close", "Close");
        }
    }
}
