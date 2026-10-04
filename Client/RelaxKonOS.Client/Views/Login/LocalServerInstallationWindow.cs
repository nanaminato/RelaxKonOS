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

namespace RelaxKonOS.Client.Views.Login;

/// <summary>Login-independent local lifecycle management with the existing source/options/review wizard.</summary>
public sealed class LocalServerInstallationWindow : Window
{
    private readonly ServerInstallationWizardViewModel _wizard;
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
        _localization = localization;
        _wizard = new ServerInstallationWizardViewModel(progress, ShowManagement,
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
        Title = T("title", "Manage this computer");
        Width = 720;
        Height = 720;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowManagement();
        Closing += (_, args) => args.Cancel = _wizard.IsBusy || _busy;
    }

    private void ShowManagement()
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = T("description", "This computer has its own Server installation. Installation and maintenance use Windows administrator permission and work without signing in or SSH."), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = _snapshot is null ? T("unknown", "Refresh to inspect the local installation.") :
            $"{T("installed", "Installed")}: {_snapshot.Installed}\n{T("healthy", "Healthy")}: {_snapshot.Healthy}\n" +
            $"{T("version", "Version")}: {_snapshot.Version}\n{T("previous", "Previous version")}: {_snapshot.PreviousVersion}\n" +
            $"ID: {_snapshot.InstallationId}\n{_snapshot.InstallRoot}\n{_snapshot.DataRoot}\n{_snapshot.ListenUrl}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = _message, TextWrapping = TextWrapping.Wrap });
        void Add(string text, Action action, bool enabled = true)
        {
            var button = new Button { Content = text, IsEnabled = enabled && !_busy, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }
        Add(T("refresh", "Refresh status"), () => _ = RunAsync(async () => _snapshot = await _installer.StatusAsync()));
        Add(T("install", "Install / update"), () => Content = new ServerInstallationWizardView(_wizard));
        var installed = _snapshot is { Installed: true, Mode: ServerInstallMode.WindowsSystem } && ServerInstallationId.IsValid(_snapshot.InstallationId);
        Add(T("repair", "Repair"), () => Review(ServerDeploymentKind.Repair), installed);
        Add(T("rollback", "Restore previous version"), () => Review(ServerDeploymentKind.Rollback), installed && !string.IsNullOrWhiteSpace(_snapshot?.PreviousVersion));
        Add(T("uninstall", "Uninstall"), () => Review(ServerDeploymentKind.Uninstall), installed);
        Add(T("connect", "Connect to local Server"), () =>
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
        Content = new ScrollViewer { Content = panel };
    }

    private void Review(ServerDeploymentKind kind)
    {
        var reviewed = _snapshot!;
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = T("review", "Review this local installation. The action may interrupt its services.") +
            "\n" + (kind == ServerDeploymentKind.Repair ? T("repair", "Repair") :
                kind == ServerDeploymentKind.Rollback ? T("rollback", "Restore previous version") : T("uninstall", "Uninstall")) +
            $"\nID: {reviewed.InstallationId}\n{reviewed.InstallRoot}\n{reviewed.DataRoot}", TextWrapping = TextWrapping.Wrap });
        var removeData = new CheckBox { Content = T("delete", "Also permanently delete managed data"), IsVisible = kind == ServerDeploymentKind.Uninstall };
        var confirmDelete = new CheckBox { Content = T("confirm_delete", "I confirm permanent deletion of the managed data shown above"), IsVisible = false };
        removeData.IsCheckedChanged += (_, _) => { confirmDelete.IsVisible = removeData.IsChecked == true; confirmDelete.IsChecked = false; };
        panel.Children.Add(removeData);
        panel.Children.Add(confirmDelete);
        var confirm = new Button { Content = T("confirm", "Confirm and request administrator permission") };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        confirm.Click += (_, _) =>
        {
            if (removeData.IsChecked == true && confirmDelete.IsChecked != true)
            {
                error.Text = T("confirm_delete", "I confirm permanent deletion of the managed data shown above");
                return;
            }
            _ = RunAsync(async () => _snapshot = await _installer.MaintainAsync(kind, reviewed,
                _localization.CurrentLanguage, true, removeData.IsChecked == true ? ServerDataRetention.Delete : ServerDataRetention.Retain));
        };
        panel.Children.Add(confirm);
        panel.Children.Add(error);
        var cancel = new Button { Content = T("cancel", "Cancel") };
        cancel.Click += (_, _) => ShowManagement();
        panel.Children.Add(cancel);
        Content = new ScrollViewer { Content = panel };
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _message = T("working", "Waiting for administrator permission or processing the local operation…");
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
