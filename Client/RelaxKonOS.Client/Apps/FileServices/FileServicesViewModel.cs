using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Services.Installation;
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
    public InstallationTaskViewModel Installation { get; set; } = null!;

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
    public ObservableCollection<FileSharePermissionEditor> SharePermissions { get; } = [];
    public bool CanManage => permissions.IsGranted(AppPermissions.ServerFileServicesManage) && !IsBusy && Capabilities is { Supported: true, ManagedSharesSupported: true } && RuntimeState is FileServiceRuntimeState.Running or FileServiceRuntimeState.Stopped;
    public FileServiceRuntimeState? RuntimeState { get; private set; }
    public string PlatformText => Capabilities is null ? T("status.loading") : Capabilities.WindowsShareSecuritySupported ? T("platform.windows") : SupportsSambaCredentials ? T("platform.linux") : T("state.Unsupported");
    public string PlatformHelp => Capabilities is null ? T("status.loading") : T(Capabilities.WindowsShareSecuritySupported ? "windows_help" : SupportsSambaCredentials ? "linux_help" : "state.Unsupported");
    // InstallSupported is the authoritative operation capability. Do not suppress the action
    // merely because an older or temporarily unhealthy server reports Supported as false.
    public bool SupportsInstall => Capabilities?.InstallSupported == true;
    public string VersionText { get; private set; } = "—";
    public Func<string, Task<bool>>? ConfirmSharePathAsync { get; set; }
    public static bool RequiresSharePathWarning(string path, bool windows)
    {
        var normalized = windows ? path.Replace('/', '\\').TrimEnd('\\') : path.TrimEnd('/');
        var separator = windows ? '\\' : '/';
        var root = windows ? @"D:\RelaxKonOSShares" : "/srv/relaxkonos-shares";
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalized.Split(separator).Contains("..") || !(normalized.Equals(root, comparison) || normalized.StartsWith(root + separator, comparison));
    }
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
    public bool IsWindowsServer => Capabilities?.WindowsShareSecuritySupported == true;
    public void AddSharePermission(string principal = "", FileShareAccess access = FileShareAccess.Read) => SharePermissions.Add(new(principal, access, IsWindowsServer));
    public bool SupportsSambaCredentials => Capabilities?.SambaCredentialsSupported == true;
    public Func<Task<string?>>? RequestHostAdministratorPasswordAsync { get; set; }
    public Func<bool, Task>? ShowShareEditorAsync { get; set; }
    public Func<Task<string?>>? ShowSharePathPickerAsync { get; set; }
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
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => Installation.SubmitAsync(InstallationOperationKind.Install, new SmbInstallationRequest(true));
    [RelayCommand(CanExecute = nameof(CanStart))] private Task StartServiceAsync() => Apply(() => client.LifecycleAsync(SmbLifecycleAction.Start));
    [RelayCommand(CanExecute = nameof(CanStop))] private Task StopAsync() => Apply(() => client.LifecycleAsync(SmbLifecycleAction.Stop));
    [RelayCommand(CanExecute = nameof(CanStop))] private Task RestartAsync() => Apply(() => client.LifecycleAsync(SmbLifecycleAction.Restart));
    [RelayCommand(CanExecute = nameof(CanManage))] private async Task NewShareAsync() { ClearEditor(); if (ShowShareEditorAsync is not null) await ShowShareEditorAsync(false); }
    [RelayCommand(CanExecute = nameof(CanEditShare))] private async Task EditShareAsync()
    {
        if (SelectedShare is null || !SelectedShare.Managed) { StatusText = LocalizedText.Get("file_services.status.managed_only", "Only RelaxKonOS-managed shares can be edited."); return; }
        ShareName = SelectedShare.Name; SharePath = SelectedShare.Path; ShareDescription = SelectedShare.Description ?? string.Empty; ShareReadOnly = SelectedShare.ReadOnly; ShareEnabled = SelectedShare.Enabled; ShareGuestAllowed = SelectedShare.GuestAllowed;
        SharePermissions.Clear();
        foreach (var permission in SelectedShare.Permissions.Where(permission => !IsGeneratedWindowsGuestPermission(permission)))
            AddSharePermission(permission.Principal, permission.Access);
        if (SharePermissions.Count == 0) AddSharePermission();
        if (ShowShareEditorAsync is not null) await ShowShareEditorAsync(true);
    }
    public async Task<bool> SaveShareAsync(bool editing)
    {
        if (!CanManage || !TryShareRequest(out var request)) return false;
        var id = SelectedShare?.Id;
        if (editing && (id is null || SelectedShare?.Managed != true)) return false;
        return await Apply(() => editing ? client.UpdateShareAsync(id!, request) : client.CreateShareAsync(request),
            request.Enabled && RequiresSharePathWarning(request.Path, IsWindowsServer)
                ? () => ConfirmSharePathAsync?.Invoke(request.Path) ?? Task.FromResult(false) : null);
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
    public async Task PickSharePathAsync()
    {
        if (ShowSharePathPickerAsync is null) return;
        var path = await ShowSharePathPickerAsync();
        if (!string.IsNullOrWhiteSpace(path)) SharePath = path;
    }
    private void ClearEditor()
    {
        ShareName = SharePath = ShareDescription = string.Empty;
        SharePermissions.Clear();
        AddSharePermission();
        ShareReadOnly = ShareGuestAllowed = false;
        ShareEnabled = true;
    }
    private bool IsGeneratedWindowsGuestPermission(FileSharePermissionDto permission) =>
        IsWindowsServer && ShareGuestAllowed && (permission.Principal is "S-1-5-7" or "S-1-5-32-546");
    private bool TryShareRequest(out UpsertFileShareRequest request)
    {
        request = default!;
        if (string.IsNullOrWhiteSpace(ShareName) || string.IsNullOrWhiteSpace(SharePath))
        { StatusText = T("validation"); return false; }
        var rules = new List<FileSharePermissionDto>();
        foreach (var item in SharePermissions)
        {
            if (string.IsNullOrWhiteSpace(item.Principal)) { StatusText = LocalizedText.Get("file_services.share_permission_invalid", "Each permission requires a principal."); return false; }
            if (IsWindowsServer && !System.Text.RegularExpressions.Regex.IsMatch(item.Principal.Trim(), @"^S-[0-9]+(-[0-9]+)+$")) { StatusText = T("windows_sid_required"); return false; }
            rules.Add(new(item.Principal.Trim(), item.SelectedAccess.Value));
        }
        request = new(ShareName.Trim(), SharePath.Trim(), string.IsNullOrWhiteSpace(ShareDescription) ? null : ShareDescription.Trim(), ShareReadOnly, ShareEnabled, ShareGuestAllowed, rules); return true;
    }
    private async Task<bool> Apply(Func<Task<FileServiceOperationResultDto>> action, Func<Task<bool>>? confirm = null)
    {
        if (!CanManage && !CanInstall()) return false;
        IsBusy = true;
        try
        {
            if (confirm is not null && !await confirm()) { StatusText = T("share_cancelled"); return false; }
            if (!await EnsureElevatedAsync())
            { StatusText = T("status.manage_required"); return false; }
            var result = await action();
            try { await LoadAsync(); }
            catch (Exception ex)
            { StatusText = T("refresh_failed") + " " + ex.Message; return result.Succeeded; }
            StatusText = result.Succeeded ? LocalizedText.Get("file_services.status.operation_completed", "SMB operation completed.") : result.ProblemCode is { } code ? Problem(code) : T("operation_failed");
            return result.Succeeded;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null && ex.Message.StartsWith("file-services.", StringComparison.Ordinal))
        { StatusText = Problem(ex.Message); return false; }
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

public sealed partial class FileSharePermissionEditor : ObservableObject
{
    public bool IsWindowsServer { get; }
    public IReadOnlyList<FileSharePrincipalOption> PrincipalOptions { get; } = [
        new("S-1-5-32-544", "file_services.principal.administrators"), new("S-1-5-32-545", "file_services.principal.users"),
        new("S-1-5-11", "file_services.principal.authenticated_users"), new("S-1-1-0", "file_services.principal.everyone")];
    [ObservableProperty] private FileSharePrincipalOption? _selectedPrincipal;
    partial void OnSelectedPrincipalChanged(FileSharePrincipalOption? value) { if (value is not null) Principal = value.Sid; }
    private readonly IReadOnlyList<FileShareAccessOption> _accessOptions = FileShareAccessOption.Create();
    public IReadOnlyList<FileShareAccessOption> AccessOptions => _accessOptions;
    [ObservableProperty] private string _principal;
    [ObservableProperty] private FileShareAccessOption _selectedAccess;

    public FileSharePermissionEditor(string principal = "", FileShareAccess access = FileShareAccess.Read, bool isWindowsServer = false)
    {
        IsWindowsServer = isWindowsServer;
        _principal = principal;
        _selectedPrincipal = PrincipalOptions.FirstOrDefault(option => string.Equals(option.Sid, principal, StringComparison.OrdinalIgnoreCase));
        _selectedAccess = _accessOptions.First(x => x.Value == access);
    }
}

public sealed record FileShareAccessOption(FileShareAccess Value, string Label)
{
    public static IReadOnlyList<FileShareAccessOption> Create() =>
    [
        new(FileShareAccess.Read, LocalizedText.Get("file_services.access.Read", "Read")),
        new(FileShareAccess.ReadWrite, LocalizedText.Get("file_services.access.ReadWrite", "Read / write"))
    ];
}

public sealed record FileSharePrincipalOption(string Sid, string LocalizationKey)
{
    public string Label => $"{LocalizedText.Get(LocalizationKey)} ({Sid})";
}
