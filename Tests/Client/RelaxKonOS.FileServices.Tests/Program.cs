using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Protocol.FileServices;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using Avalonia;

AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
SynchronizationContext.SetSynchronizationContext(null);
using var services = new ServiceCollection()
    .AddSingleton(new LocalizationService(new ShellSettings(null!), new SshDesktopSession(null!)))
    .BuildServiceProvider();
typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var client = new FakeClient();
var vm = new FileServicesViewModel(client, new Permissions(), () => true) { RequestHostAdministratorCredentialsAsync = _ => Task.FromResult<HostAdministratorCredentials?>(new("host-admin", "test")) };
Check(!vm.NewShareCommand.CanExecute(null), "No mutations before discovery");
await vm.StartAsync();
Check(!vm.SupportsSambaCredentials && !vm.InstallCommand.CanExecute(null) && client.UserReads == 0, "Windows capabilities");
Check(vm.StopCommand.CanExecute(null) && !vm.StartServiceCommand.CanExecute(null), "Running lifecycle");
await vm.StopCommand.ExecuteAsync(null);
Check(client.StatusReads == 2 && vm.StartServiceCommand.CanExecute(null), "Mutation refresh bypasses busy gate");
await vm.StopCommand.ExecuteAsync(null);
await vm.RestartCommand.ExecuteAsync(null);
Check(client.Writes == 1, "Direct stopped-service commands cannot bypass runtime gates");
vm.AddSharePermission();
Check(vm.SharePermissions[0].IsWindowsServer, "Windows principal selector enabled");
vm.SharePermissions[0].SelectedPrincipal = vm.SharePermissions[0].PrincipalOptions[0];
Check(vm.SharePermissions[0].Principal == "S-1-5-32-544", "Group choice submits SID");
vm.ShareName = "normal share"; vm.SharePath = @"D:\";
vm.SharePermissions[0].Principal = "Administrator";
Check(!await vm.SaveShareAsync(false) && client.Writes == 1, "Account name receives SID validation before submission");
vm.SharePermissions.Clear();
client.Linux = true;
await vm.RefreshCommand.ExecuteAsync(null);
Check(vm.SupportsSambaCredentials && client.UserReads == 1, "Linux users loaded");
vm.SelectedUser = vm.Users.Single();
var sambaPasswordPrompt = new TaskCompletionSource<string?>();
vm.RequestSambaPasswordAsync = () => sambaPasswordPrompt.Task;
var writesBeforePasswordPrompt = client.Writes;
var passwordUpdate = vm.SetSambaPasswordCommand.ExecuteAsync(null);
Check(vm.IsAwaitingInput && !vm.IsBusy && !vm.ToggleUserCommand.CanExecute(null) && client.Writes == writesBeforePasswordPrompt,
    "Entering a Samba password is local input and must not show operation progress or submit a request");
sambaPasswordPrompt.SetResult("a-valid-samba-password");
await passwordUpdate;
Check(!vm.IsAwaitingInput && !vm.IsBusy && client.Writes == writesBeforePasswordPrompt + 1,
    "Samba password update begins only after the local password prompt closes");
vm.AddSharePermission();
Check(vm.SharePermissions[^1].HasPrincipalOptions && vm.SharePermissions[^1].PrincipalOptions.Single().Value == "nanami", "Eligible Linux users are available as permission choices");
var removedUserPrompt = new TaskCompletionSource<string?>();
vm.SelectedUser = vm.Users.Single();
vm.RequestSambaPasswordAsync = () => removedUserPrompt.Task;
var writesBeforeRemovedUser = client.Writes;
var removedUserUpdate = vm.SetSambaPasswordCommand.ExecuteAsync(null);
vm.Users.Clear();
removedUserPrompt.SetResult("test-only-password");
await removedUserUpdate;
Check(client.Writes == writesBeforeRemovedUser && !vm.IsAwaitingInput, "A removed user cannot receive a password submitted from an old prompt");
await vm.RefreshCommand.ExecuteAsync(null);
vm.SelectedUser = new("system", false, false);
Check(!vm.ToggleUserCommand.CanExecute(null), "Ineligible user blocked");
vm.ShareName = "test"; vm.SharePath = "/srv/relaxkonos-shares/test"; vm.SharePermissions.Clear(); vm.AddSharePermission();
var writesBeforeShare = client.Writes;
Check(!await vm.SaveShareAsync(false) && client.Writes == writesBeforeShare, "Empty permission rejected");
vm.SharePermissions[0].Principal = "user"; vm.ShareGuestAllowed = true;
vm.SharePermissions[0].SelectedAccess = vm.SharePermissions[0].AccessOptions.Single(x => x.Value == FileShareAccess.ReadWrite);
var authorization = new TaskCompletionSource<HostAdministratorCredentials?>();
vm.RequestHostAdministratorCredentialsAsync = _ => authorization.Task;
var save = vm.SaveShareAsync(false);
Check(vm.IsAwaitingInput && !vm.IsBusy && !vm.NewShareCommand.CanExecute(null), "Serialized while waiting for authorization");
Check(!await vm.SaveShareAsync(false), "Concurrent save blocked");
authorization.SetResult(new("host-admin", "test"));
Check(await save && client.Writes == writesBeforeShare + 1, "Guest-enabled writable share saves without global read-only");
vm.SelectedShare = new("id", "test", "/test", null, true, true, false, [], true);
vm.ConfirmDeleteAsync = _ => Task.FromResult(false);
await vm.DeleteShareCommand.ExecuteAsync(null);
Check(client.Writes == writesBeforeShare + 1, "Cancelled delete does not mutate");
client.State = FileServiceRuntimeState.NotInstalled;
await vm.RefreshCommand.ExecuteAsync(null);
Check(vm.InstallCommand.CanExecute(null) && !vm.NewShareCommand.CanExecute(null), "Not installed actions");
client.Supported = false;
await vm.RefreshCommand.ExecuteAsync(null);
Check(vm.SupportsInstall && vm.InstallCommand.CanExecute(null), "The explicit install capability remains available when a server reports an unhealthy platform state");
client.Supported = true;
await InstallationRoutingChecks.RunAsync(vm);
await ReadRecoveryChecks.RunAsync();
var originalShare = new FileShareDto("original", "original", "/srv/relaxkonos-shares/original", null, false, true, false, [new("nanami", FileShareAccess.Read)], true);
var otherShare = originalShare with { Id = "other", Name = "other" };
var targetClient = new FakeClient { Linux = true, ShareData = [originalShare, otherShare] };
var targetVm = new FileServicesViewModel(targetClient, new Permissions(), () => true)
{ RequestHostAdministratorCredentialsAsync = _ => Task.FromResult<HostAdministratorCredentials?>(new("admin", "test")) };
await targetVm.StartAsync();
targetVm.SelectedShare = originalShare;
targetVm.ShowShareEditorAsync = async editing =>
{
    var beforeWrongMode = targetClient.Writes;
    Check(!await targetVm.SaveShareAsync(false) && targetClient.Writes == beforeWrongMode,
        "An edit dialog cannot be submitted as a new share through a mismatched mode");
    targetVm.SelectedShare = otherShare;
    Check(await targetVm.SaveShareAsync(editing) && targetClient.UpdatedId == "original", "An open editor retains its original share target");
    targetClient.ShareData = [otherShare];
    await targetVm.RefreshCommand.ExecuteAsync(null);
    var writes = targetClient.Writes;
    Check(!await targetVm.SaveShareAsync(editing) && targetClient.Writes == writes, "A removed original share cannot redirect the draft to another selection");
};
await targetVm.EditShareCommand.ExecuteAsync(null);
targetClient.ShareData = [originalShare, otherShare];
await targetVm.RefreshCommand.ExecuteAsync(null);
targetVm.SelectedShare = originalShare;
targetVm.ShowShareEditorAsync = async editing =>
{
    targetVm.ShareDescription = "local unsaved change";
    targetClient.ShareData = [originalShare with { Permissions = [new("nanami", FileShareAccess.ReadWrite)] }, otherShare];
    await targetVm.RefreshCommand.ExecuteAsync(null);
    var before = targetClient.Writes;
    Check(!await targetVm.SaveShareAsync(editing) && targetClient.Writes == before
        && targetVm.ShareDescription == "local unsaved change", "Refreshed remote permission changes block the old editor and preserve its draft");
};
await targetVm.EditShareCommand.ExecuteAsync(null);
targetClient.ShareData = [originalShare, otherShare];
await targetVm.RefreshCommand.ExecuteAsync(null);
targetVm.SelectedShare = originalShare;
var pickedPath = new TaskCompletionSource<string?>();
Task? pendingPicker = null;
targetVm.ShowSharePathPickerAsync = () => pickedPath.Task;
targetVm.ShowShareEditorAsync = _ => { pendingPicker = targetVm.PickSharePathAsync(); return Task.CompletedTask; };
await targetVm.EditShareCommand.ExecuteAsync(null);
targetVm.SharePath = "new draft path";
pickedPath.SetResult("late old path");
await pendingPicker!;
Check(targetVm.SharePath == "new draft path", "A picker returning after the editor closes cannot overwrite a new draft");
var pathChange = new TaskCompletionSource<string?>();
targetVm.ShowSharePathPickerAsync = () => pathChange.Task;
var pathTask = targetVm.PickSharePathAsync();
targetVm.SharePath = "manually corrected path";
pathChange.SetResult("picked path");
await pathTask;
Check(targetVm.SharePath == "manually corrected path", "Late picker result preserves a manual path correction");
targetClient.ShareData = [originalShare, otherShare];
await targetVm.RefreshCommand.ExecuteAsync(null);
targetVm.SelectedShare = originalShare;
var deleteConfirmation = new TaskCompletionSource<bool>();
targetVm.ConfirmDeleteAsync = _ => deleteConfirmation.Task;
var deletion = targetVm.DeleteShareCommand.ExecuteAsync(null);
Check(targetVm.IsAwaitingInput && !targetVm.IsBusy && !targetVm.RefreshCommand.CanExecute(null)
    && !targetVm.NewShareCommand.CanExecute(null), "Deletion confirmation serializes commands without displaying remote progress");
targetVm.SelectedShare = otherShare;
deleteConfirmation.SetResult(true);
await deletion;
Check(targetClient.DeletedId == "original" && !targetVm.IsAwaitingInput && !targetVm.IsBusy,
    "Deletion uses the confirmed share even when selection changes");
targetVm.SelectedShare = targetVm.Shares.Single(share => share.Id == originalShare.Id);
var changedDeleteConfirmation = new TaskCompletionSource<bool>();
targetVm.ConfirmDeleteAsync = _ => changedDeleteConfirmation.Task;
var writesBeforeChangedDelete = targetClient.Writes;
var changedDeletion = targetVm.DeleteShareCommand.ExecuteAsync(null);
targetVm.Shares[0] = originalShare with { Path = "/srv/relaxkonos-shares/replaced" };
changedDeleteConfirmation.SetResult(true);
await changedDeletion;
Check(targetClient.Writes == writesBeforeChangedDelete && !targetVm.IsAwaitingInput,
    "Deletion refuses a share whose observed configuration changed after confirmation opened");
Check(!FileServicesViewModel.RequiresSharePathWarning(@"d:\RelaxKonOSShares\folder", true), "Default Windows subtree needs no warning");
Check(FileServicesViewModel.RequiresSharePathWarning(@"D:\RelaxKonOSSharesOther", true), "Sibling prefix is outside default root");
Check(FileServicesViewModel.RequiresSharePathWarning(@"D:\RelaxKonOSShares\..\private", true), "Traversal cannot skip warning");
Check(!FileServicesViewModel.RequiresSharePathWarning("/srv/relaxkonos-shares/folder", false), "Default Linux subtree needs no warning");
foreach (var linux in new[] { false, true })
{
    var warningClient = new FakeClient { Linux = linux };
    var warningVm = new FileServicesViewModel(warningClient, new Permissions(), () => true);
    await warningVm.StartAsync();
    warningVm.ShareName = "共享"; warningVm.SharePath = linux ? "/mnt/data" : @"E:\Test";
    var passwordRequests = 0;
    warningVm.RequestHostAdministratorCredentialsAsync = _ => { passwordRequests++; return Task.FromResult<HostAdministratorCredentials?>(new("host-admin", "test")); };
    var confirmation = new TaskCompletionSource<bool>();
    warningVm.ConfirmSharePathAsync = _ => confirmation.Task;
    var pendingSave = warningVm.SaveShareAsync(false);
    Check(warningVm.IsAwaitingInput && !warningVm.IsBusy && passwordRequests == 0 && warningClient.Writes == 0, "Path confirmation precedes password and mutation");
    Check(!await warningVm.SaveShareAsync(false), "Concurrent save cannot bypass warning");
    confirmation.SetResult(false);
    Check(!await pendingSave && warningClient.Writes == 0 && passwordRequests == 0, "Cancel leaves share untouched");
    warningVm.ConfirmSharePathAsync = _ => Task.FromResult(true);
    Check(await warningVm.SaveShareAsync(false) && warningClient.Writes == 1 && passwordRequests == 1, "Confirmed non-default path is shared");
}
var retryClient = new FakeClient { InvalidElevationAttempts = 1 };
var retryVm = new FileServicesViewModel(retryClient, new Permissions(), () => true);
await retryVm.StartAsync();
var promptErrors = new List<string?>();
retryVm.RequestHostAdministratorCredentialsAsync = error =>
{
    promptErrors.Add(error);
    return Task.FromResult<HostAdministratorCredentials?>(new("host-admin", error is null ? "incorrect" : "correct"));
};
await retryVm.StopCommand.ExecuteAsync(null);
Check(promptErrors.Count == 2 && promptErrors[0] is null && !string.IsNullOrWhiteSpace(promptErrors[1])
    && retryClient.ElevationCredentials.SequenceEqual([new("host-admin", "incorrect"), new("host-admin", "correct")]) && retryClient.Writes == 1,
    "An invalid host administrator credential shows an error and requests a replacement before the operation runs");
var nonAdministratorClient = new FakeClient { NonAdministratorElevationAttempts = 1 };
var nonAdministratorVm = new FileServicesViewModel(nonAdministratorClient, new Permissions(), () => true);
await nonAdministratorVm.StartAsync();
var nonAdministratorErrors = new List<string?>();
nonAdministratorVm.RequestHostAdministratorCredentialsAsync = error =>
{
    nonAdministratorErrors.Add(error);
    return Task.FromResult<HostAdministratorCredentials?>(new(error is null ? "standard-user" : "host-admin", "correct"));
};
await nonAdministratorVm.StopCommand.ExecuteAsync(null);
Check(nonAdministratorErrors.Count == 2 && nonAdministratorErrors[0] is null && !string.IsNullOrWhiteSpace(nonAdministratorErrors[1])
    && nonAdministratorClient.ElevationCredentials.SequenceEqual([new("standard-user", "correct"), new("host-admin", "correct")]) && nonAdministratorClient.Writes == 1,
    "A non-administrator account asks for a different administrator credential before the operation runs");
Console.WriteLine("Passed: platform discovery, lifecycle refresh, user eligibility, validation, authorization serialization, password retry, delete cancellation and installation routing.");

sealed class Permissions : IAppPermissionScope
{
 public AppPermissionStatus GetStatus(string id) => AppPermissionStatus.Granted;
 public bool IsGranted(string id) => true;
 public Task<AppPermissionStatus> RequestAsync(string id, CancellationToken ct = default) => Task.FromResult(AppPermissionStatus.Granted);
 public Task OpenSettingsAsync() => Task.CompletedTask;
}
sealed class FakeClient : IRemoteFileServicesClient
{
 public string? FailRead;
 public Action<string>? BeforeRead;
 public string? UpdatedId;
 public string? DeletedId;
 public bool LoseWriteReceipt;
 public HttpStatusCode? RejectWrite;
 public Action? BeforeWriteReceipt;
 public IReadOnlyList<FileShareDto> ShareData = [];
 private void Read(string stage) { BeforeRead?.Invoke(stage); if (FailRead == stage) throw new HttpRequestException("test read failure"); }
 public bool Linux; public bool Supported = true; public int StatusReads, UserReads, Writes, InvalidElevationAttempts, NonAdministratorElevationAttempts;
 public List<HostAdministratorCredentials> ElevationCredentials { get; } = [];
 public FileServiceRuntimeState State = FileServiceRuntimeState.Running;
 public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct = default) { Read("status"); StatusReads++; return Task.FromResult(new FileServiceStatusDto(FileServiceProtocol.Smb, State, "test", State == FileServiceRuntimeState.Running, true)); }
 public Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct = default) { Read("capabilities"); return Task.FromResult(new FileServiceCapabilitiesDto(Supported, Linux, Linux, true, !Linux)); }
 public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct = default) { Read("shares"); return Task.FromResult(ShareData); }
 public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct = default) { Read("users"); UserReads++; return Task.FromResult<IReadOnlyList<FileServiceUserDto>>(Linux ? [new("nanami", false, true)] : []); }
 public Task<FileServiceConnectionInfoDto> GetConnectionAsync(CancellationToken ct = default) { Read("connection"); return Task.FromResult(new FileServiceConnectionInfoDto("host",445,"\\\\host\\","smb://host/")); }
 private Task<FileServiceOperationResultDto> Result() { Writes++; BeforeWriteReceipt?.Invoke(); if (RejectWrite is { } status) throw new HttpRequestException("test refusal", null, status); if (LoseWriteReceipt) throw new HttpRequestException("test lost receipt"); return Task.FromResult(new FileServiceOperationResultDto(Guid.NewGuid(),true)); }
 public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, CancellationToken ct = default) { State = action == SmbLifecycleAction.Stop ? FileServiceRuntimeState.Stopped : FileServiceRuntimeState.Running; return Result(); }
 public Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest r,CancellationToken ct = default) => Result();
 public Task<FileServiceOperationResultDto> UpdateShareAsync(string id,UpsertFileShareRequest r,CancellationToken ct = default) { UpdatedId = id; return Result(); }
 public Task<FileServiceOperationResultDto> DeleteShareAsync(string id,CancellationToken ct = default) { DeletedId = id; return Result(); }
 public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username,bool enabled,CancellationToken ct = default) => Result();
 public Task<FileServiceOperationResultDto> SetSambaPasswordAsync(string username,SetSambaPasswordRequest r,CancellationToken ct = default) => Result();
 public Task<bool> ElevateAsync(HostAdministratorCredentials credentials,CancellationToken ct = default)
 {
     ElevationCredentials.Add(credentials);
     if (InvalidElevationAttempts-- > 0) throw new HttpRequestException("elevation-password-invalid", null, HttpStatusCode.Forbidden);
     if (NonAdministratorElevationAttempts-- > 0) throw new HttpRequestException("elevation-account-not-administrator", null, HttpStatusCode.Forbidden);
     return Task.FromResult(true);
 }
}
