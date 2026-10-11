using System.ComponentModel;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Client.Services.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;

namespace RelaxKonOS.Client.Views.Login;

/// <summary>Login-independent local lifecycle management with the existing source/options/review wizard.</summary>
public sealed class LocalServerInstallationWindow : Window
{
    private ServerInstallationWizardViewModel _wizard;
    private readonly Func<ServerInstallationWizardViewModel> _wizardFactory;
    private readonly NativeDraftCloseGuard _closeGuard;
    private string? _endpoint;
    private readonly LocalWindowsServerInstaller _installer;
    private readonly LoginLocalizationService _localization;
    private ServerHostSnapshotDto? _snapshot;
    private bool _busy;
    private string _message = string.Empty;
    private string T(string key, string fallback) => _localization.Get("login.local_manage_" + key, fallback);

    public LocalServerInstallationWindow()
    {
        var progress = App.Services.GetRequiredService<ServerCenterViewModel>();
        var localization = App.Services.GetRequiredService<LoginLocalizationService>();
        var installer = App.Services.GetRequiredService<LocalWindowsServerInstaller>();
        _installer = installer;
        _installer.Mode = ServerInstallMode.WindowsUser;
        _localization = localization;
        _wizardFactory = () => new ServerInstallationWizardViewModel(progress, FinishWizard,
            CancelWizardAsync,
            () => Task.FromResult<string?>(null), () => Task.CompletedTask, async options =>
            {
                progress.ErrorMessage = string.Empty;
                try
                {
                    _endpoint = await installer.InstallAsync(options, localization.CurrentLanguage,
                        new Progress<string>(stage => progress.StatusMessage = stage == "elevating"
                            ? progress.Text("login.local_install_elevating", "Approve the Windows administrator permission request to continue.")
                            : progress.Text("server_center.progress." + stage, stage)),
                        new Progress<ServerDeploymentTransfer?>(transfer =>
                        {
                            progress.IsDeploymentTransferActive = transfer?.Total is > 0;
                            progress.DeploymentTransferProgress = transfer?.Total is > 0
                                ? Math.Clamp(transfer.Bytes * 100d / transfer.Total.Value, 0, 100) : 0;
                            if (transfer is not null) progress.StatusMessage = progress.Text("server_center.progress.downloading", "Downloading the installation package…") +
                                (transfer.Total is > 0 ? $" {transfer.Bytes:N0} / {transfer.Total:N0} B" : $" {transfer.Bytes:N0} B");
                        }));
                    _snapshot = installer.LastVerifiedSnapshot;
                    _message = T("completed", "Local operation completed and status verified.");
                    return true;
                }
                catch (Win32Exception error) when (error.NativeErrorCode == 1223)
                {
                    progress.ErrorMessage = progress.Text("login.local_install_uac_cancelled", "Administrator permission was cancelled. You can review the options and try again.");
                }
                catch (LocalWindowsDeploymentFailedException error)
                {
                    progress.ErrorMessage = error.Receipt.SafeMessage ?? progress.Text("login.local_install_failed", "Local installation failed. Check the deployment operation record.");
                }
                catch (Exception error)
                {
                    progress.ErrorMessage = progress.Text("login.local_install_failed", "Local installation failed. Check the deployment operation record.") + " (" + error.GetType().Name + ")";
                }
                finally { progress.IsDeploymentTransferActive = false; }
                if (installer.LastOperationId is { } operationId)
                    progress.ErrorMessage += "\n" + progress.Text("login.local_install_operation", "Operation ID") + ": " + operationId;
                return false;
            });
        _wizard = _wizardFactory();
        _closeGuard = new NativeDraftCloseGuard(this, () => _wizard.IsBusy || _busy,
            () => Content is ServerInstallationWizardView && _wizard.HasDraftChanges,
            ConfirmDiscardAsync, () => _wizard.ClearSecrets());
        Title = T("title", "Manage this computer");
        Width = 820;
        Height = 720;
        MinWidth = 380;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowManagement();
    }

    private void FinishWizard()
    {
        _wizard.ClearSecrets();
        ShowManagement();
    }

    private async Task<bool> CancelWizardAsync()
    {
        if (!await _closeGuard.TryDiscardAsync()) return false;
        FinishWizard();
        return true;
    }

    private Task<bool> ConfirmDiscardAsync()
    {
        var confirmation = new Window
        {
            Title = _localization.Get("common.discard_title", "Discard changes?"),
            Width = 480, Height = 240, MinWidth = 460, MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        confirmation.Content = new ConfirmDialogView
        {
            DataContext = new ConfirmDialogViewModel(_localization.Get("common.discard_message", "Discard unsaved changes and close this editor?"),
                accepted => confirmation.Close(accepted), _localization.Get("common.discard", "Discard"),
                _localization.Get("common.cancel", "Cancel"))
        };
        confirmation.KeyDown += (_, args) =>
        {
            if (args.Key != Avalonia.Input.Key.Escape) return;
            args.Handled = true;
            confirmation.Close(false);
        };
        return confirmation.ShowDialog<bool>(this);
    }

    private void ShowManagement()
    {
        var panel = new StackPanel { Spacing = 16 };
        var summary = new StackPanel { Spacing = 10 };
        var mode = new ComboBox
        {
            ItemsSource = new[] { T("personal_mode", "Personal mode · current account"), T("service_mode", "System service · always available") },
            SelectedIndex = _installer.Mode == ServerInstallMode.WindowsUser ? 0 : 1,
            HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !_busy
        };
        mode.SelectionChanged += (_, _) =>
        {
            var selected = mode.SelectedIndex == 0 ? ServerInstallMode.WindowsUser : ServerInstallMode.WindowsSystem;
            if (selected == _installer.Mode) return;
            _installer.Mode = selected; _snapshot = null; _message = string.Empty;
            _wizard.SelectedMode = _wizard.Modes.First(option => option.Mode == selected);
            ShowManagement();
        };
        summary.Children.Add(mode);
        summary.Children.Add(LocalManagementLayout.Text(_installer.Mode == ServerInstallMode.WindowsUser
            ? T("personal_hint", "Runs as your Windows account. Starts at sign-in; ends at sign-out. Separate program and data; ordinary maintenance needs no UAC.")
            : T("service_hint", "Runs as a Windows service even when nobody is signed in. Installation and maintenance require UAC."), 13, true));
        var state = _snapshot is null ? T("unknown", "Refresh to inspect the local installation.") :
            !_snapshot.Installed ? T("not_installed", "Server is not installed yet") :
            _snapshot.Healthy ? T("ready", "Local Server is ready") : T("needs_attention", "Local Server needs attention");
        summary.Children.Add(LocalManagementLayout.Header(state,
            _snapshot?.Installed == true ? T("version", "Version") + ": " + _snapshot.Version : T("start_hint", "Install Server to start using this computer."), "host"));
        if (_snapshot is { Installed: true })
        {
            summary.Children.Add(LocalManagementLayout.Text(_snapshot.ListenUrl ?? string.Empty));
            var details = new StackPanel { Spacing = 8 };
            details.Children.Add(LocalManagementLayout.Text(T("previous", "Previous version") + ": " + (_snapshot.PreviousVersion ?? T("none", "None"))));
            details.Children.Add(LocalManagementLayout.Text("ID: " + _snapshot.InstallationId, 12, true));
            details.Children.Add(LocalManagementLayout.Text(T("program_directory", "Program directory") + "\n" + _snapshot.InstallRoot, 12, true));
            details.Children.Add(LocalManagementLayout.Text(T("data_directory", "Data directory") + "\n" + _snapshot.DataRoot, 12, true));
            summary.Children.Add(new Expander { Header = T("details", "Installation details"), Content = details, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        panel.Children.Add(LocalManagementLayout.Card(summary));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.SizeChanged += (_, _) => actions.ItemWidth = Math.Max(1, actions.Bounds.Width / (actions.Bounds.Width >= 620 ? 2 : 1));
        void Add(string text, string description, string icon, Action action, bool enabled = true) =>
            actions.Children.Add(LocalManagementLayout.Action(text, description, icon, action, enabled && !_busy));
        Add(T("refresh", "Refresh status"), T("refresh_hint", "Check the current installation and service health"), "monitor", () => _ = RunAsync(async () => _snapshot = await _installer.StatusAsync()));
        Add(T("install", "Install / update"), T("install_hint", "Choose a release package and review installation options"), "deployments", () =>
        {
            _wizard.ClearSecrets();
            _wizard = _wizardFactory();
            _wizard.SelectedMode = _wizard.Modes.First(option => option.Mode == _installer.Mode);
            if (Uri.TryCreate(_snapshot?.ListenUrl, UriKind.Absolute, out var installedUrl))
            {
                _wizard.ServerPortText = installedUrl.Port.ToString();
                _wizard.SelectedCertificateMode = _wizard.CertificateModes.First(option => option.Mode ==
                    (installedUrl.Scheme == Uri.UriSchemeHttps ? ServerCertificateMode.SelfSigned : ServerCertificateMode.None));
            }
            _wizard.ResetDraftBaseline();
            Content = new ServerInstallationWizardView(_wizard, null);
        });
        var installed = _snapshot is { Installed: true, Mode: ServerInstallMode.WindowsSystem or ServerInstallMode.WindowsUser } && ServerInstallationId.IsValid(_snapshot.InstallationId);
        Add(T("repair", "Repair"), T("repair_hint", "Restore services, certificates and firewall rules"), "diagnostics", () => _ = ReviewAsync(ServerDeploymentKind.Repair), installed);
        Add(T("rollback", "Restore previous version"), T("rollback_hint", "Switch back to the last verified version"), "operations", () => _ = ReviewAsync(ServerDeploymentKind.Rollback), installed && !string.IsNullOrWhiteSpace(_snapshot?.PreviousVersion));
        Add(T("uninstall", "Uninstall"), T("uninstall_hint", "Remove Server; keep data by default"), "host_settings", () => _ = ReviewAsync(ServerDeploymentKind.Uninstall), installed);
        Add(T("connect", "Connect to local Server"), T("connect_hint", "Continue to sign in with your Windows account"), "connections", () =>
        {
            try
            {
                _endpoint = LocalWindowsServerInstaller.VerifiedLocalEndpoint(_snapshot!.InstallationId!, _snapshot);
                Close(_endpoint);
            }
            catch (InvalidDataException)
            {
                _snapshot = null;
                _message = T("failed", "Local operation failed. Refresh status before retrying.");
                ShowManagement();
            }
        }, installed && _snapshot!.Healthy);
        panel.Children.Add(actions);
        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3, IsVisible = _busy });
        footer.Children.Add(LocalManagementLayout.Text(string.IsNullOrEmpty(_message) ? T("local_hint", "Runs independently on this computer. Closing the client does not stop Server.") : _message, 13, true));
        Content = LocalManagementLayout.Shell(LocalManagementLayout.Header(T("title", "Manage this computer"), T("subtitle", "Your local Server, all in one place"), "host"), panel, footer);
    }

    private async Task ReviewAsync(ServerDeploymentKind kind)
    {
        if (_busy || _snapshot is null) return;
        var window = new LocalServerMaintenanceWindow(kind, _snapshot, _installer, _localization);
        _busy = true;
        try
        {
            await window.ShowDialog(this);
            _snapshot = window.Snapshot;
            if (!string.IsNullOrEmpty(window.Message)) _message = window.Message;
        }
        finally { _busy = false; ShowManagement(); }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _message = _installer.Mode == ServerInstallMode.WindowsUser
            ? T("personal_working", "Processing the personal Server operation…")
            : T("working", "Waiting for administrator permission or processing the local operation…");
        ShowManagement();
        try
        {
            await action();
            _message = T("completed", "Local operation completed and status verified.");

        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            _message = T("cancelled", "Administrator permission was cancelled.");
        }
        catch (Exception error)
        {
            _snapshot = null;
            _message = error is LocalWindowsDeploymentFailedException failure ? failure.Receipt.SafeMessage ?? T("failed", "Local operation failed. Refresh status before retrying.") :
                T("failed", "Local operation failed. Refresh status before retrying.") + " (" + error.GetType().Name + ")";
            if (_installer.LastOperationId is { } id) _message += "\n" + T("operation", "Operation ID") + ": " + id;
        }
        finally { _busy = false; ShowManagement(); }
    }
}
