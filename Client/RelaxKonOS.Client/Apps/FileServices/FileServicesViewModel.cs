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
public sealed partial class FileServicesViewModel(IRemoteFileServicesClient client, IAppPermissionScope permissions, Func<bool> isWindowSessionCurrent) : LocalizedObservableObject
{
    public InstallationTaskViewModel Installation { get; set; } = null!;

    public ObservableCollection<FileShareDto> Shares { get; } = [];
    public ObservableCollection<FileServiceUserDto> Users { get; } = [];
    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.RefWithFallback("file_services.status.loading", "Loading SMB status…");
    [ObservableProperty] private string _connectionText = "—";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NewShareCommand), nameof(RefreshCommand), nameof(InstallCommand), nameof(StartServiceCommand), nameof(StopCommand), nameof(RestartCommand), nameof(EditShareCommand), nameof(DeleteShareCommand), nameof(ToggleUserCommand), nameof(SetSambaPasswordCommand))] private bool _isBusy;
    // A modal confirmation or elevation prompt is local UI, not a submitted host operation.
    // Keep commands serialized while it is open without showing the operation progress bar.
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NewShareCommand), nameof(RefreshCommand), nameof(InstallCommand), nameof(StartServiceCommand), nameof(StopCommand), nameof(RestartCommand), nameof(EditShareCommand), nameof(DeleteShareCommand), nameof(ToggleUserCommand), nameof(SetSambaPasswordCommand))] private bool _isAwaitingInput;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SupportsSambaCredentials))] private FileServiceCapabilitiesDto? _capabilities;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EditShareCommand), nameof(DeleteShareCommand))] private FileShareDto? _selectedShare;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UserToggleText))] [NotifyCanExecuteChangedFor(nameof(ToggleUserCommand), nameof(SetSambaPasswordCommand))] private FileServiceUserDto? _selectedUser;
    [ObservableProperty] private string _shareName = string.Empty;
    [ObservableProperty] private string _sharePath = string.Empty;
    [ObservableProperty] private string _shareDescription = string.Empty;
    [ObservableProperty] private bool _shareReadOnly;
    [ObservableProperty] private bool _shareEnabled = true;
    [ObservableProperty] private bool _shareGuestAllowed;
    public bool CanCloseShareDraft => !IsBusy && !IsAwaitingInput;
    public bool CanEditShareDraft => isWindowSessionCurrent() && CanCloseShareDraft;
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEditShareDraft)); OnPropertyChanged(nameof(CanCloseShareDraft)); OnPropertyChanged(nameof(CanManage)); }
    partial void OnIsAwaitingInputChanged(bool value) { OnPropertyChanged(nameof(CanEditShareDraft)); OnPropertyChanged(nameof(CanCloseShareDraft)); OnPropertyChanged(nameof(CanManage)); }
    public ObservableCollection<FileSharePermissionEditor> SharePermissions { get; } = [];
    public bool FactsAvailable { get; private set; }
    private void EnsureWindowSession()
    {
        if (isWindowSessionCurrent()) return;
        InvalidateWindowSession();
        throw new InvalidOperationException(LocalizedText.Get("file_services.session_changed"));
    }
    public void InvalidateWindowSession()
    {
        FactsAvailable = false;
        Capabilities = null; RuntimeState = null; VersionText = "—"; ConnectionText = "—";
        Shares.Clear(); Users.Clear(); SelectedShare = null; SelectedUser = null;
        ShareName = SharePath = ShareDescription = string.Empty;
        ShareReadOnly = ShareGuestAllowed = false; ShareEnabled = true;
        SharePermissions.Clear();
        _shareEditorVersion++;
        StatusText = Ref("session_changed");
        OnPropertyChanged(nameof(CanEditShareDraft));
        NotifyActions();
    }
    public bool CanManage => isWindowSessionCurrent() && FactsAvailable && permissions.IsGranted(AppPermissions.ServerFileServicesManage) && !IsBusy && !IsAwaitingInput && Capabilities is { Supported: true, ManagedSharesSupported: true } && RuntimeState is FileServiceRuntimeState.Running or FileServiceRuntimeState.Stopped;
    public FileServiceRuntimeState? RuntimeState { get; private set; }
    public string PlatformText => !isWindowSessionCurrent() ? T("session_ended") : Capabilities is null ? T("status.loading") : Capabilities.WindowsShareSecuritySupported ? T("platform.windows") : SupportsSambaCredentials ? T("platform.linux") : T("state.Unsupported");
    public string PlatformHelp => !isWindowSessionCurrent() ? T("session_changed") : Capabilities is null ? T("status.loading") : T(Capabilities.WindowsShareSecuritySupported ? "windows_help" : SupportsSambaCredentials ? "linux_help" : "state.Unsupported");
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
    private bool CanInstall() => isWindowSessionCurrent() && FactsAvailable && !IsBusy && !IsAwaitingInput && permissions.IsGranted(AppPermissions.ServerFileServicesManage) && SupportsInstall && RuntimeState == FileServiceRuntimeState.NotInstalled;
    private bool CanStart() => CanManage && RuntimeState == FileServiceRuntimeState.Stopped;
    private bool CanStop() => CanManage && RuntimeState == FileServiceRuntimeState.Running;
    private static string T(string key) => LocalizedText.Get("file_services." + key);
    private static LocalizedStatus Ref(string key) => LocalizedText.Ref("file_services." + key);
    private static LocalizedStatus Problem(string code) => LocalizedText.Ref("file_services.problem." + code, code);
    private void NotifyActions()
    {
        foreach (var command in new IRelayCommand[] { RefreshCommand, InstallCommand, StartServiceCommand, StopCommand, RestartCommand, NewShareCommand, EditShareCommand, DeleteShareCommand, ToggleUserCommand, SetSambaPasswordCommand }) command.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlatformText)); OnPropertyChanged(nameof(PlatformHelp)); OnPropertyChanged(nameof(SupportsInstall)); OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(FactsAvailable)); OnPropertyChanged(nameof(CanManage));
    }
    public bool IsWindowsServer => Capabilities?.WindowsShareSecuritySupported == true;
    public void AddSharePermission(string principal = "", FileShareAccess access = FileShareAccess.Read) => SharePermissions.Add(new(principal, access, IsWindowsServer,
        SupportsSambaCredentials ? Users.Where(user => user.Eligible).Select(user => user.Username) : []));
    public bool SupportsSambaCredentials => Capabilities?.SambaCredentialsSupported == true;
    public string UserToggleText => LocalizedText.Get(SelectedUser?.Enabled == true ? "file_services.user_disable" : "file_services.user_enable");
    /// <summary>Requests one-time host administrator credentials. The optional argument explains why a previous entry was rejected.</summary>
    public Func<string?, Task<HostAdministratorCredentials?>>? RequestHostAdministratorCredentialsAsync { get; set; }
    public Func<bool, Task>? ShowShareEditorAsync { get; set; }
    public Func<Task<string?>>? ShowSharePathPickerAsync { get; set; }
    public Func<Task<string?>>? RequestSambaPasswordAsync { get; set; }
    private bool _shareEditorOpen;
    private string? _editingShareId;
    private FileShareDto? _editingShareBaseline;
    private long _shareEditorVersion;
    private bool _pickingSharePath;
    public async Task StartAsync() => await RefreshAsync();
    [RelayCommand(CanExecute = nameof(CanRead))] private async Task RefreshAsync()
    {
        if (!CanRead()) { StatusText = LocalizedText.RefWithFallback("file_services.status.read_required", "File Services read permission is required."); return; }
        IsBusy = true;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex) { if (!isWindowSessionCurrent()) InvalidateWindowSession(); else StatusText = LocalizedText.RefWithFallback("file_services.refresh_failed", "Could not refresh SMB status. {0}", ex.Message); }
        finally { IsBusy = false; NotifyActions(); }
    }
    private async Task LoadAsync()
    {
        EnsureWindowSession();
        FactsAvailable = false;
        NotifyActions();
        var capabilities = await client.GetCapabilitiesAsync();
        EnsureWindowSession();
        var status = await client.GetStatusAsync();
        EnsureWindowSession();
        IReadOnlyList<FileShareDto> shares = [];
        IReadOnlyList<FileServiceUserDto> users = [];
        if (capabilities.Supported && status.State is FileServiceRuntimeState.Running or FileServiceRuntimeState.Stopped)
        {
            if (capabilities.ManagedSharesSupported) shares = await client.ListSharesAsync();
            EnsureWindowSession();
            if (capabilities.SambaCredentialsSupported) users = await client.ListUsersAsync();
            EnsureWindowSession();
        }
        var connection = await client.GetConnectionAsync();
        EnsureWindowSession();
        // Publish a complete snapshot only after every required read succeeds. A failed
        // refresh preserves the last display, but it cannot authorize another mutation.
        var selectedShareId = SelectedShare?.Id;
        var selectedUsername = SelectedUser?.Username;
        Capabilities = capabilities;
        RuntimeState = status.State; VersionText = status.Version ?? "—";
        Shares.Clear(); Users.Clear();
        foreach (var share in shares) Shares.Add(share);
        foreach (var user in users) Users.Add(user);
        SelectedShare = Shares.FirstOrDefault(share => share.Id == selectedShareId);
        SelectedUser = Users.FirstOrDefault(user => user.Username == selectedUsername);
        ConnectionText = connection.WindowsUncPrefix + " · " + connection.SmbUriPrefix;
        StatusText = status.HealthProblemCode is { } code ? Problem(code) : LocalizedText.Ref("file_services.status.ready", LocalizedText.Get("file_services.state." + status.State));
        FactsAvailable = true;
        NotifyActions();
    }
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => CanInstall() ? Installation.SubmitAsync(InstallationOperationKind.Install, new SmbInstallationRequest(true)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanStart))] private Task StartServiceAsync() => CanStart() ? Apply(() => client.LifecycleAsync(SmbLifecycleAction.Start),
        targetCurrent: () => FactsAvailable && RuntimeState == FileServiceRuntimeState.Stopped, targetFailure: "operation_failed") : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanStop))] private Task StopAsync() => CanStop() ? Apply(() => client.LifecycleAsync(SmbLifecycleAction.Stop),
        targetCurrent: () => FactsAvailable && RuntimeState == FileServiceRuntimeState.Running, targetFailure: "operation_failed") : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanStop))] private Task RestartAsync() => CanStop() ? Apply(() => client.LifecycleAsync(SmbLifecycleAction.Restart),
        targetCurrent: () => FactsAvailable && RuntimeState == FileServiceRuntimeState.Running, targetFailure: "operation_failed") : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanManage))] private async Task NewShareAsync()
    {
        if (!CanManage || _shareEditorOpen) return;
        ClearEditor();
        await OpenShareEditorAsync(false);
    }
    [RelayCommand(CanExecute = nameof(CanEditShare))] private async Task EditShareAsync()
    {
        if (!CanManage || _shareEditorOpen) return;
        if (SelectedShare is null || !SelectedShare.Managed) { StatusText = LocalizedText.RefWithFallback("file_services.status.managed_only", "Only RelaxKonOS-managed shares can be edited."); return; }
        ShareName = SelectedShare.Name; SharePath = SelectedShare.Path; ShareDescription = SelectedShare.Description ?? string.Empty; ShareReadOnly = SelectedShare.ReadOnly; ShareEnabled = SelectedShare.Enabled; ShareGuestAllowed = SelectedShare.GuestAllowed;
        SharePermissions.Clear();
        foreach (var permission in SelectedShare.Permissions.Where(permission => !IsGeneratedWindowsGuestPermission(permission)))
            AddSharePermission(permission.Principal, permission.Access);
        if (SharePermissions.Count == 0) AddSharePermission();
        await OpenShareEditorAsync(true);
    }
    private async Task OpenShareEditorAsync(bool editing)
    {
        if (ShowShareEditorAsync is null) return;
        _shareEditorOpen = true;
        _shareEditorVersion++;
        _editingShareId = editing ? SelectedShare?.Id : null;
        _editingShareBaseline = editing && SelectedShare is { } original
            ? original with { Permissions = original.Permissions.ToArray() } : null;
        try { await ShowShareEditorAsync(editing); }
        finally { _shareEditorOpen = false; _editingShareId = null; _editingShareBaseline = null; _shareEditorVersion++; }
    }
    public async Task<bool> SaveShareAsync(bool editing)
    {
        if (_shareEditorOpen && editing != (_editingShareId is not null))
        { StatusText = Ref("share_target_changed"); return false; }
        if (!CanManage || !TryShareRequest(out var request)) return false;
        var id = _shareEditorOpen ? _editingShareId : SelectedShare?.Id;
        bool TargetCurrent() => FactsAvailable && (!editing || (id is not null
            && Shares.Any(share => share.Id == id && share.Managed
                && (_editingShareBaseline is null || SameShare(share, _editingShareBaseline)))));
        if (editing && (id is null || !TargetCurrent())) { StatusText = Ref("share_target_changed"); return false; }
        return await Apply(() => editing ? client.UpdateShareAsync(id!, request) : client.CreateShareAsync(request),
            request.Enabled && RequiresSharePathWarning(request.Path, IsWindowsServer)
                ? () => ConfirmSharePathAsync?.Invoke(request.Path) ?? Task.FromResult(false) : null, TargetCurrent);
    }
    private static bool SameShare(FileShareDto current, FileShareDto original) =>
        current.Id == original.Id && current.Name == original.Name && current.Path == original.Path
        && current.Description == original.Description && current.ReadOnly == original.ReadOnly
        && current.Enabled == original.Enabled && current.GuestAllowed == original.GuestAllowed
        && current.Managed == original.Managed && current.Drifted == original.Drifted
        && current.Permissions.SequenceEqual(original.Permissions);
    [RelayCommand(CanExecute = nameof(CanEditShare))] private async Task DeleteShareAsync()
    {
        if (!CanEditShare() || SelectedShare is not { Managed: true } share || ConfirmDeleteAsync is null) return;
        var baseline = share with { Permissions = share.Permissions.ToArray() };
        await Apply(() => client.DeleteShareAsync(share.Id), () => ConfirmDeleteAsync(share.Name),
            () => FactsAvailable && Shares.Any(current => current.Id == share.Id && current.Managed && SameShare(current, baseline)));
    }
    [RelayCommand(CanExecute = nameof(CanUser))] private Task ToggleUserAsync() => CanUser() && SelectedUser is { } user
        ? Apply(() => client.SetUserEnabledAsync(user.Username, !user.Enabled), targetCurrent: () => UserCurrent(user), targetFailure: "user_target_changed") : Task.CompletedTask;
    private bool UserCurrent(FileServiceUserDto user) => FactsAvailable && SupportsSambaCredentials
        && Users.Any(current => current.Username == user.Username && current.Eligible && current.Enabled == user.Enabled);
    [RelayCommand(CanExecute = nameof(CanUser))] private async Task SetSambaPasswordAsync()
    {
        if (!CanUser() || SelectedUser is not { } user || RequestSambaPasswordAsync is null) return;
        IsAwaitingInput = true;
        string? password;
        try { password = await RequestSambaPasswordAsync(); }
        finally { IsAwaitingInput = false; }
        if (string.IsNullOrEmpty(password)) return;
        try
        {
            if (!isWindowSessionCurrent()) { InvalidateWindowSession(); return; }
            if (!UserCurrent(user)) { StatusText = Ref("user_target_changed"); return; }
            await Apply(() => client.SetSambaPasswordAsync(user.Username, new SetSambaPasswordRequest(password)),
                targetCurrent: () => UserCurrent(user), targetFailure: "user_target_changed");
        }
        finally { password = null!; }
    }
    private bool CanRead() => isWindowSessionCurrent() && permissions.IsGranted(AppPermissions.ServerFileServicesRead) && !IsBusy && !IsAwaitingInput;
    private bool CanEditShare() => CanManage && SelectedShare is { Managed: true };
    private bool CanUser() => CanManage && Capabilities?.SambaCredentialsSupported == true && SelectedUser is { Eligible: true };
    public async Task PickSharePathAsync()
    {
        if (ShowSharePathPickerAsync is null || !CanEditShareDraft || _pickingSharePath) return;
        var version = _shareEditorVersion;
        var originalPath = SharePath;
        _pickingSharePath = true;
        try
        {
            var path = await ShowSharePathPickerAsync();
            if (version == _shareEditorVersion && CanEditShareDraft && SharePath == originalPath
                && !string.IsNullOrWhiteSpace(path)) SharePath = path;
        }
        finally { _pickingSharePath = false; }
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
        { StatusText = Ref("validation"); return false; }
        var rules = new List<FileSharePermissionDto>();
        foreach (var item in SharePermissions)
        {
            if (string.IsNullOrWhiteSpace(item.Principal)) { StatusText = LocalizedText.RefWithFallback("file_services.share_permission_invalid", "Each permission requires a principal."); return false; }
            if (IsWindowsServer && !System.Text.RegularExpressions.Regex.IsMatch(item.Principal.Trim(), @"^S-[0-9]+(-[0-9]+)+$")) { StatusText = Ref("windows_sid_required"); return false; }
            rules.Add(new(item.Principal.Trim(), item.SelectedAccess.Value));
        }
        request = new(ShareName.Trim(), SharePath.Trim(), string.IsNullOrWhiteSpace(ShareDescription) ? null : ShareDescription.Trim(), ShareReadOnly, ShareEnabled, ShareGuestAllowed, rules); return true;
    }
    private async Task<bool> Apply(Func<Task<FileServiceOperationResultDto>> action, Func<Task<bool>>? confirm = null, Func<bool>? targetCurrent = null, string targetFailure = "share_target_changed")
    {
        if (!CanManage && !CanInstall()) return false;
        var awaitingReceipt = false;
        IsAwaitingInput = true;
        try
        {
            EnsureWindowSession();
            if (confirm is not null)
            {
                var confirmed = await confirm();
                EnsureWindowSession();
                if (!confirmed) { StatusText = Ref("share_cancelled"); return false; }
            }
            EnsureWindowSession();
            var elevated = await EnsureElevatedAsync();
            EnsureWindowSession();
            if (!elevated)
            { StatusText = Ref("status.manage_required"); return false; }
            EnsureWindowSession();
            if (!permissions.IsGranted(AppPermissions.ServerFileServicesManage)) { StatusText = Ref("status.manage_required"); return false; }
            if (targetCurrent is not null && !targetCurrent()) { StatusText = Ref(targetFailure); return false; }
            IsBusy = true;
            IsAwaitingInput = false;
            FactsAvailable = false;
            NotifyActions();
            awaitingReceipt = true;
            var result = await action();
            awaitingReceipt = false;
            EnsureWindowSession();
            try { await LoadAsync(); }
            catch (Exception)
            {
                if (!isWindowSessionCurrent()) { InvalidateWindowSession(); return false; }
                StatusText = result.Succeeded ? Ref("success_refresh_failed") : LocalizedStatus.Join(" ",
                    [result.ProblemCode is { } problem ? Problem(problem) : Ref("operation_failed"), Ref("refresh_failed")]);
                return result.Succeeded;
            }
            StatusText = result.Succeeded ? LocalizedText.RefWithFallback("file_services.status.operation_completed", "SMB operation completed.") : result.ProblemCode is { } code ? Problem(code) : Ref("operation_failed");
            return result.Succeeded;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null && ex.Message.StartsWith("file-services.", StringComparison.Ordinal))
        { if (!isWindowSessionCurrent()) InvalidateWindowSession(); else StatusText = Problem(ex.Message); return false; }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status && (int)status is 400 or 401 or 403 or 404 or 405 or 409 or 412 or 422 or 429)
        { if (!isWindowSessionCurrent()) InvalidateWindowSession(); else StatusText = LocalizedText.Ref("file_services.request_rejected", (int)ex.StatusCode!.Value); return false; }
        catch (Exception ex)
        {
            if (!isWindowSessionCurrent()) { InvalidateWindowSession(); return false; }
            StatusText = awaitingReceipt ? Ref("operation_unknown") : LocalizedStatus.Literal(ex.Message);
            return false;
        }
        finally { IsAwaitingInput = false; IsBusy = false; NotifyActions(); }
    }
    private async Task<bool> EnsureElevatedAsync()
    {
        string? error = null;
        while (true)
        {
            EnsureWindowSession();
            var credentials = await (RequestHostAdministratorCredentialsAsync?.Invoke(error) ?? Task.FromResult<HostAdministratorCredentials?>(null));
            if (credentials is null || string.IsNullOrEmpty(credentials.Password)) return false;
            EnsureWindowSession();
            try { return await client.ElevateAsync(credentials); }
            catch (HttpRequestException ex) when (ex.Message == "elevation-password-invalid")
            {
                error = LocalizedText.Get("file_services.host_password_invalid");
            }
            catch (HttpRequestException ex) when (ex.Message == "elevation-account-not-administrator")
            {
                error = LocalizedText.Get("file_services.host_account_not_administrator");
            }
        }
    }
}

public sealed partial class FileSharePermissionEditor : ObservableObject
{
    public bool IsWindowsServer { get; }
    public IReadOnlyList<FileSharePrincipalOption> PrincipalOptions { get; }
    public bool HasPrincipalOptions => PrincipalOptions.Count > 0;
    [ObservableProperty] private FileSharePrincipalOption? _selectedPrincipal;
    partial void OnSelectedPrincipalChanged(FileSharePrincipalOption? value) { if (value is not null) Principal = value.Value; }
    private readonly IReadOnlyList<FileShareAccessOption> _accessOptions = FileShareAccessOption.Create();
    public IReadOnlyList<FileShareAccessOption> AccessOptions => _accessOptions;
    [ObservableProperty] private string _principal;
    [ObservableProperty] private FileShareAccessOption _selectedAccess;

    public FileSharePermissionEditor(string principal = "", FileShareAccess access = FileShareAccess.Read, bool isWindowsServer = false, IEnumerable<string>? linuxUsers = null)
    {
        IsWindowsServer = isWindowsServer;
        PrincipalOptions = isWindowsServer
            ? [new("S-1-5-32-544", LocalizedText.Get("file_services.principal.administrators")), new("S-1-5-32-545", LocalizedText.Get("file_services.principal.users")),
               new("S-1-5-11", LocalizedText.Get("file_services.principal.authenticated_users")), new("S-1-1-0", LocalizedText.Get("file_services.principal.everyone"))]
            : (linuxUsers ?? []).Distinct(StringComparer.Ordinal).OrderBy(username => username, StringComparer.Ordinal).Select(username => new FileSharePrincipalOption(username, username)).ToArray();
        _principal = principal;
        _selectedPrincipal = PrincipalOptions.FirstOrDefault(option => string.Equals(option.Value, principal, StringComparison.OrdinalIgnoreCase));
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

public sealed record FileSharePrincipalOption(string Value, string Label);
