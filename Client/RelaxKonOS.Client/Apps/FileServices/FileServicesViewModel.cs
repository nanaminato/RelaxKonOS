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
    public ObservableCollection<FileServiceUserDto> Users { get; } = [];
    [ObservableProperty] private string _statusText = "Loading SMB status…";
    [ObservableProperty] private string _connectionText = "—";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(InstallCommand), nameof(StartServiceCommand), nameof(StopCommand), nameof(RestartCommand), nameof(EditShareCommand), nameof(DeleteShareCommand), nameof(ToggleUserCommand), nameof(SetSambaPasswordCommand))] private bool _isBusy;
    [ObservableProperty] private FileServiceCapabilitiesDto? _capabilities;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EditShareCommand), nameof(DeleteShareCommand))] private FileShareDto? _selectedShare;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ToggleUserCommand), nameof(SetSambaPasswordCommand))] private FileServiceUserDto? _selectedUser;
    [ObservableProperty] private string _shareName = string.Empty;
    [ObservableProperty] private string _sharePath = string.Empty;
    [ObservableProperty] private string _shareDescription = string.Empty;
    [ObservableProperty] private bool _shareReadOnly;
    [ObservableProperty] private bool _shareEnabled = true;
    [ObservableProperty] private bool _shareGuestAllowed;
    [ObservableProperty] private string _sharePrincipals = string.Empty;
    public bool CanManage => permissions.IsGranted(AppPermissions.ServerFileServicesManage) && !IsBusy;
    public Func<Task<string?>>? RequestHostAdministratorPasswordAsync { get; set; }
    public Func<bool, Task>? ShowShareEditorAsync { get; set; }
    public Func<Task<string?>>? RequestSambaPasswordAsync { get; set; }
    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand(CanExecute = nameof(CanRead))] private async Task RefreshAsync()
    {
        if (!CanRead()) { StatusText = "File Services read permission is required."; return; }
        IsBusy = true;
        try
        {
            var status = await client.GetStatusAsync(); Capabilities = await client.GetCapabilitiesAsync();
            Shares.Clear(); foreach (var share in await client.ListSharesAsync()) Shares.Add(share);
            Users.Clear(); if (Capabilities.SambaCredentialsSupported) foreach (var user in await client.ListUsersAsync()) Users.Add(user);
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
    [RelayCommand(CanExecute = nameof(CanManage))] private async Task NewShareAsync() { ClearEditor(); if (ShowShareEditorAsync is not null) await ShowShareEditorAsync(false); }
    [RelayCommand(CanExecute = nameof(CanEditShare))] private async Task EditShareAsync()
    {
        if (SelectedShare is null || !SelectedShare.Managed) { StatusText = "Only RelaxKonOS-managed shares can be edited."; return; }
        ShareName = SelectedShare.Name; SharePath = SelectedShare.Path; ShareDescription = SelectedShare.Description ?? string.Empty; ShareReadOnly = SelectedShare.ReadOnly; ShareEnabled = SelectedShare.Enabled; ShareGuestAllowed = SelectedShare.GuestAllowed;
        SharePrincipals = string.Join(',', SelectedShare.Permissions.Select(x => x.Principal + ":" + x.Access)); if (ShowShareEditorAsync is not null) await ShowShareEditorAsync(true);
    }
    public async Task<bool> SaveShareAsync(bool editing)
    {
        if (!TryShareRequest(out var request)) return false;
        await Apply(() => editing && SelectedShare is { } share ? client.UpdateShareAsync(share.Id, request) : client.CreateShareAsync(request)); return !StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }
    [RelayCommand(CanExecute = nameof(CanEditShare))] private Task DeleteShareAsync() => SelectedShare is { Managed: true } share ? Apply(() => client.DeleteShareAsync(share.Id)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanUser))] private Task ToggleUserAsync() => SelectedUser is { } user ? Apply(() => client.SetUserEnabledAsync(user.Username, !user.Enabled)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanUser))] private async Task SetSambaPasswordAsync()
    {
        if (SelectedUser is null || RequestSambaPasswordAsync is null) return; var password = await RequestSambaPasswordAsync();
        if (string.IsNullOrEmpty(password)) return;
        try { await Apply(() => client.SetSambaPasswordAsync(SelectedUser.Username, new SetSambaPasswordRequest(password))); }
        finally { password = null!; }
    }
    private bool CanRead() => permissions.IsGranted(AppPermissions.ServerFileServicesRead) && !IsBusy;
    private bool CanEditShare() => CanManage && SelectedShare is { Managed: true };
    private bool CanUser() => CanManage && Capabilities?.SambaCredentialsSupported == true && SelectedUser is not null;
    private void ClearEditor() { ShareName = SharePath = ShareDescription = SharePrincipals = string.Empty; ShareReadOnly = ShareGuestAllowed = false; ShareEnabled = true; }
    private bool TryShareRequest(out UpsertFileShareRequest request)
    {
        var rules = new List<FileSharePermissionDto>();
        foreach (var item in SharePrincipals.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split(':', 2, StringSplitOptions.TrimEntries); if (pair.Length != 2 || !Enum.TryParse<FileShareAccess>(pair[1], true, out var access)) { StatusText = "Permissions use principal:Read or principal:ReadWrite."; request = default!; return false; }
            rules.Add(new(pair[0], access));
        }
        request = new(ShareName.Trim(), SharePath.Trim(), string.IsNullOrWhiteSpace(ShareDescription) ? null : ShareDescription.Trim(), ShareReadOnly, ShareEnabled, ShareGuestAllowed, rules); return true;
    }
    private async Task Apply(Func<Task<FileServiceOperationResultDto>> action)
    {
        if (!await EnsureElevatedAsync()) { StatusText = "SMB host-administrator authorization is required."; return; }
        IsBusy = true;
        try { var result = await action(); StatusText = result.Succeeded ? "SMB operation completed." : result.ProblemCode ?? "SMB operation failed."; await RefreshAsync(); }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task<bool> EnsureElevatedAsync()
    {
        var password = await (RequestHostAdministratorPasswordAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (string.IsNullOrEmpty(password)) return false;
        try { return await client.ElevateAsync(password); } finally { password = null!; }
    }
}
