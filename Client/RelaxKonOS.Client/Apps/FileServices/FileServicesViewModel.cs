using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Client.Apps.FileServices;

/// <summary>Window-local SMB control-plane state. It never retains Samba passwords or Helper output.</summary>
public sealed partial class FileServicesViewModel(IRemoteFileServicesClient client, IAppPermissionScope permissions) : ObservableObject
{
    public ObservableCollection<FileShareDto> Shares { get; } = [];
    [ObservableProperty] private string _statusText = "Loading SMB status…";
    [ObservableProperty] private string _connectionText = "—";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(InstallCommand), nameof(StartServiceCommand), nameof(StopCommand), nameof(RestartCommand))] private bool _isBusy;
    [ObservableProperty] private FileServiceCapabilitiesDto? _capabilities;
    public bool CanManage => permissions.IsGranted(AppPermissions.ServerFileServicesManage) && !IsBusy;
    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand(CanExecute = nameof(CanRead))] private async Task RefreshAsync()
    {
        if (!CanRead()) { StatusText = "File Services read permission is required."; return; }
        IsBusy = true;
        try
        {
            var status = await client.GetStatusAsync(); Capabilities = await client.GetCapabilitiesAsync();
            Shares.Clear(); foreach (var share in await client.ListSharesAsync()) Shares.Add(share);
            var connection = await client.GetConnectionAsync(); ConnectionText = connection.WindowsUncPrefix + "share  ·  " + connection.SmbUriPrefix + "share";
            StatusText = status.HealthProblemCode ?? $"SMB {status.State}";
        }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanManage))] private Task InstallAsync() => Apply(() => client.InstallAsync());
    [RelayCommand(CanExecute = nameof(CanManage))] private Task StartServiceAsync() => Apply(() => client.LifecycleAsync("start"));
    [RelayCommand(CanExecute = nameof(CanManage))] private Task StopAsync() => Apply(() => client.LifecycleAsync("stop"));
    [RelayCommand(CanExecute = nameof(CanManage))] private Task RestartAsync() => Apply(() => client.LifecycleAsync("restart"));
    private bool CanRead() => permissions.IsGranted(AppPermissions.ServerFileServicesRead) && !IsBusy;
    private async Task Apply(Func<Task<FileServiceOperationResultDto>> action)
    {
        IsBusy = true;
        try { var result = await action(); StatusText = result.Succeeded ? "SMB operation completed." : result.ProblemCode ?? "SMB operation failed."; await RefreshAsync(); }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsBusy = false; }
    }
}
