using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Client.Apps.FileServices;

/// <summary>Window-local SMB control-plane state. It never retains Samba passwords or Helper output.</summary>
public sealed partial class FileServicesViewModel(IRemoteFileServicesClient client, IAppPermissionScope permissions) : ObservableObject
{
    public ObservableCollection<FileShareDto> Shares { get; } = [];
    public ObservableCollection<FileServiceUserDto> Users { get; } = [];
    [ObservableProperty] private string _statusText = LocalizedText.Get("file_services.status.loading", "Loading SMB status…");
    [ObservableProperty] private string _connectionText = "—";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NewShareCommand), nameof(RefreshCommand), nameof(InstallCommand), nameof(StartServiceCommand), nameof(StopCommand), nameof(RestartCommand), nameof(EditShareCommand), nameof(DeleteShareCommand), nameof(ToggleUserCommand), nameof(SetSambaPasswordCommand))] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SupportsSambaCredentials))] private FileServiceCapabilitiesDto? _capabilities;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EditShareCommand), nameof(DeleteShareCommand))] private FileShareDto? _selectedShare;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ToggleUserCommand), nameof(SetSambaPasswordCommand))] private FileServiceUserDto? _selectedUser;
    [ObservableProperty] private string _shareName = string.Empty;
    [ObservableProperty] private string _sharePath = string.Empty;
    [ObservableProperty] private string _shareDescription = string.Empty;
    [ObservableProperty] private bool _shareReadOnly;
    [ObservableProperty] private bool _shareEnabled = true;
    [ObservableProperty] private bool _shareGuestAllowed;
    [ObservableProperty] private string _sharePrincipals = string.Empty;
    public bool CanManage => permissions.IsGranted(AppPermissions.ServerFileServicesManage) && !IsBusy && Capabilities is { Supported: true, ManagedSharesSupported: true } && RuntimeState is FileServiceRuntimeState.Running or FileServiceRuntimeState.Stopped;
    public FileServiceRuntimeState? RuntimeState { get; private set; }
    public string PlatformText => Capabilities is null ? T("status.loading") : Capabilities.WindowsShareSecuritySupported ? "Windows SMB" : SupportsSambaCredentials ? "Linux · Samba" : T("state.Unsupported");
    public string PlatformHelp => Capabilities is null ? T("status.loading") : T(Capabilities.WindowsShareSecuritySupported ? "windows_help" : SupportsSambaCredentials ? "linux_help" : "state.Unsupported");
    public bool SupportsInstall => Capabilities is { Supported: true, InstallSupported: true };
    public string VersionText { get; private set; } = "—";
    public Func<string, Task<bool>>? ConfirmDeleteAsync { get; set; }
    private bool CanInstall() => !IsBusy && permissions.IsGranted(AppPermissions.ServerFileServicesManage) && SupportsInstall && RuntimeState == FileServiceRuntimeState.NotInstalled;
    private bool CanStart() => CanManage && RuntimeState == FileServiceRuntimeState.Stopped;
    private bool CanStop() => CanManage && RuntimeState == FileServiceRuntimeState.Running;
    private static string T(string key) => LocalizedText.Get("file_services." + key);
    private static string Problem(string code) => LocalizedText.Get("file_services.problem." + code, code);
    private void NotifyActions()
    {
        foreach (var command in new IRelayCommand[] { RefreshCommand, InstallCommand, StartServiceCommand, StopCommand, RestartCommand, NewShareCommand, EditShareCommand, DeleteShareCommand, ToggleUserCommand, SetSambaPasswordCommand }) command.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlatformText)); OnPropertyChanged(nameof(PlatformHelp)); OnPropertyChanged(nameof(SupportsInstall)); OnPropertyChanged(nameof(VersionText));
    }
    public bool SupportsSambaCredentials => Capabilities?.SambaCredentialsSupported == true;
    public Func<Task<string?>>? RequestHostAdministratorPasswordAsync { get; set; }
    public Func<bool, Task>? ShowShareEditorAsync { get; set; }
    public Func<Task<string?>>? RequestSambaPasswordAsync { get; set; }
    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand(CanExecute = nameof(CanRead))] private async Task RefreshAsync()
    {
        if (!CanRead()) { StatusText = LocalizedText.Get("file_services.status.read_required", "File Services read permission is required."); return; }
        IsBusy = true;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsBusy = false; NotifyActions(); }
    }
    private async Task LoadAsync()
    {
        RuntimeState = null;
        Capabilities = await client.GetCapabilitiesAsync();
        var status = await client.GetStatusAsync();
        RuntimeState = status.State; VersionText = status.Version ?? "—";
        StatusText = status.HealthProblemCode is { } code ? Problem(code) : LocalizedText.Format("file_services.status.ready", T("state." + status.State));
        SelectedShare = null; SelectedUser = null; Shares.Clear(); Users.Clear();
        if (Capabilities.Supported && status.State is FileServiceRuntimeState.Running or FileServiceRuntimeState.Stopped)
        {
            if (Capabilities.ManagedSharesSupported) foreach (var share in await client.ListSharesAsync()) Shares.Add(share);
            if (SupportsSambaCredentials) foreach (var user in await client.ListUsersAsync()) Users.Add(user);
        }
        var connection = await client.GetConnectionAsync();
        ConnectionText = connection.WindowsUncPrefix + " · " + connection.SmbUriPrefix;
        NotifyActions();
    }
    [RelayCommand(CanExecute = nameof(CanInstall))] private Task InstallAsync() => Apply(() => client.InstallAsync());
    [RelayCommand(CanExecute = nameof(CanStart))] private Task StartServiceAsync() => Apply(() => client.LifecycleAsync(SmbLifecycleAction.Start));
    [RelayCommand(CanExecute = nameof(CanStop))] private Task StopAsync() => Apply(() => client.LifecycleAsync(SmbLifecycleAction.Stop));
    [RelayCommand(CanExecute = nameof(CanStop))] private Task RestartAsync() => Apply(() => client.LifecycleAsync(SmbLifecycleAction.Restart));
    [RelayCommand(CanExecute = nameof(CanManage))] private async Task NewShareAsync() { ClearEditor(); if (ShowShareEditorAsync is not null) await ShowShareEditorAsync(false); }
    [RelayCommand(CanExecute = nameof(CanEditShare))] private async Task EditShareAsync()
    {
        if (SelectedShare is null || !SelectedShare.Managed) { StatusText = LocalizedText.Get("file_services.status.managed_only", "Only RelaxKonOS-managed shares can be edited."); return; }
        ShareName = SelectedShare.Name; SharePath = SelectedShare.Path; ShareDescription = SelectedShare.Description ?? string.Empty; ShareReadOnly = SelectedShare.ReadOnly; ShareEnabled = SelectedShare.Enabled; ShareGuestAllowed = SelectedShare.GuestAllowed;
        SharePrincipals = string.Join(',', SelectedShare.Permissions.Select(x => x.Principal + ":" + x.Access)); if (ShowShareEditorAsync is not null) await ShowShareEditorAsync(true);
    }
    public async Task<bool> SaveShareAsync(bool editing)
    {
        if (!CanManage || !TryShareRequest(out var request)) return false;
        var id = SelectedShare?.Id;
        if (editing && (id is null || SelectedShare?.Managed != true)) return false;
        return await Apply(() => editing ? client.UpdateShareAsync(id!, request) : client.CreateShareAsync(request));
    }
    [RelayCommand(CanExecute = nameof(CanEditShare))] private async Task DeleteShareAsync()
    {
        if (SelectedShare is not { Managed: true } share || ConfirmDeleteAsync is null) return;
        if (await ConfirmDeleteAsync(share.Name)) await Apply(() => client.DeleteShareAsync(share.Id));
    }
    [RelayCommand(CanExecute = nameof(CanUser))] private Task ToggleUserAsync() => SelectedUser is { } user ? Apply(() => client.SetUserEnabledAsync(user.Username, !user.Enabled)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanUser))] private async Task SetSambaPasswordAsync()
    {
        if (SelectedUser is not { } user || RequestSambaPasswordAsync is null) return; var password = await RequestSambaPasswordAsync();
        if (string.IsNullOrEmpty(password)) return;
        try { await Apply(() => client.SetSambaPasswordAsync(user.Username, new SetSambaPasswordRequest(password))); }
        finally { password = null!; }
    }
    private bool CanRead() => permissions.IsGranted(AppPermissions.ServerFileServicesRead) && !IsBusy;
    private bool CanEditShare() => CanManage && SelectedShare is { Managed: true };
    private bool CanUser() => CanManage && Capabilities?.SambaCredentialsSupported == true && SelectedUser is { Eligible: true };
    private void ClearEditor() { ShareName = SharePath = ShareDescription = SharePrincipals = string.Empty; ShareReadOnly = ShareGuestAllowed = false; ShareEnabled = true; }
    private bool TryShareRequest(out UpsertFileShareRequest request)
    {
        request = default!;
        if (string.IsNullOrWhiteSpace(ShareName) || string.IsNullOrWhiteSpace(SharePath) || ShareGuestAllowed && !ShareReadOnly)
        { StatusText = T("validation"); return false; }
        var rules = new List<FileSharePermissionDto>();
        foreach (var item in SharePrincipals.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split(':', 2, StringSplitOptions.TrimEntries); if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || !Enum.TryParse<FileShareAccess>(pair[1], true, out var access) || !Enum.IsDefined(access)) { StatusText = LocalizedText.Get("file_services.share_permission_invalid", "Permissions use principal:Read or principal:ReadWrite."); request = default!; return false; }
            rules.Add(new(pair[0], access));
        }
        request = new(ShareName.Trim(), SharePath.Trim(), string.IsNullOrWhiteSpace(ShareDescription) ? null : ShareDescription.Trim(), ShareReadOnly, ShareEnabled, ShareGuestAllowed, rules); return true;
    }
    private async Task<bool> Apply(Func<Task<FileServiceOperationResultDto>> action)
    {
        if (!CanManage && !CanInstall()) return false;
        IsBusy = true;
        try
        {
            if (!await EnsureElevatedAsync())
            { StatusText = T("status.manage_required"); return false; }
            var result = await action();
            try { await LoadAsync(); }
            catch (Exception ex)
            { StatusText = T("refresh_failed") + " " + ex.Message; return result.Succeeded; }
            StatusText = result.Succeeded ? LocalizedText.Get("file_services.status.operation_completed", "SMB operation completed.") : result.ProblemCode is { } code ? Problem(code) : T("operation_failed");
            return result.Succeeded;
        }
        catch (Exception ex) { StatusText = ex.Message; return false; }
        finally { IsBusy = false; NotifyActions(); }
    }
    private async Task<bool> EnsureElevatedAsync()
    {
        var password = await (RequestHostAdministratorPasswordAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (string.IsNullOrEmpty(password)) return false;
        try { return await client.ElevateAsync(password); } finally { password = null!; }
    }
}
