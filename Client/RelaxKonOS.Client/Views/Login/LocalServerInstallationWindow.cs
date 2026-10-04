using System.ComponentModel;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;

namespace RelaxKonOS.Client.Views.Login;

/// <summary>Login-independent local installation with the existing source/options/review wizard.</summary>
public sealed class LocalServerInstallationWindow : Window
{
    private readonly ServerInstallationWizardViewModel _wizard;
    private string? _endpoint;

    public LocalServerInstallationWindow()
    {
        var progress = App.Services.GetRequiredService<ServerCenterViewModel>();
        var localization = App.Services.GetRequiredService<LoginLocalizationService>();
        var installer = App.Services.GetRequiredService<LocalWindowsServerInstaller>();
        _wizard = new ServerInstallationWizardViewModel(progress, () => Close(_endpoint),
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
        Title = _wizard.Title;
        Width = 720;
        Height = 720;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ServerInstallationWizardView(_wizard);
        Closing += (_, args) => args.Cancel = _wizard.IsBusy;
    }
}
