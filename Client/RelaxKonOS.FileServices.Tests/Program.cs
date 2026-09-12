using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Protocol.FileServices;
using System.Net;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var client = new FakeClient();
var vm = new FileServicesViewModel(client, new Permissions()) { RequestHostAdministratorPasswordAsync = () => Task.FromResult<string?>("test") };
Check(!vm.NewShareCommand.CanExecute(null), "No mutations before discovery");
await vm.StartAsync();
Check(!vm.SupportsSambaCredentials && !vm.InstallCommand.CanExecute(null) && client.UserReads == 0, "Windows capabilities");
Check(vm.StopCommand.CanExecute(null) && !vm.StartServiceCommand.CanExecute(null), "Running lifecycle");
await vm.StopCommand.ExecuteAsync(null);
Check(client.StatusReads == 2 && vm.StartServiceCommand.CanExecute(null), "Mutation refresh bypasses busy gate");
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
vm.AddSharePermission();
Check(vm.SharePermissions[^1].HasPrincipalOptions && vm.SharePermissions[^1].PrincipalOptions.Single().Value == "nanami", "Eligible Linux users are available as permission choices");
vm.SelectedUser = new("system", false, false);
Check(!vm.ToggleUserCommand.CanExecute(null), "Ineligible user blocked");
vm.ShareName = "test"; vm.SharePath = "/srv/relaxkonos-shares/test"; vm.SharePermissions.Add(new());
Check(!await vm.SaveShareAsync(false) && client.Writes == 1, "Empty permission rejected");
vm.SharePermissions[0].Principal = "user"; vm.ShareGuestAllowed = true;
vm.SharePermissions[0].SelectedAccess = FileShareAccessOption.All.Single(x => x.Value == FileShareAccess.ReadWrite);
var authorization = new TaskCompletionSource<string?>();
vm.RequestHostAdministratorPasswordAsync = () => authorization.Task;
var save = vm.SaveShareAsync(false);
Check(vm.IsBusy && !vm.NewShareCommand.CanExecute(null), "Busy during authorization");
Check(!await vm.SaveShareAsync(false), "Concurrent save blocked");
authorization.SetResult("test");
Check(await save && client.Writes == 2, "Guest-enabled writable share saves without global read-only");
vm.SelectedShare = new("id", "test", "/test", null, true, true, false, [], true);
vm.ConfirmDeleteAsync = _ => Task.FromResult(false);
await vm.DeleteShareCommand.ExecuteAsync(null);
Check(client.Writes == 2, "Cancelled delete does not mutate");
client.State = FileServiceRuntimeState.NotInstalled;
await vm.RefreshCommand.ExecuteAsync(null);
Check(vm.InstallCommand.CanExecute(null) && !vm.NewShareCommand.CanExecute(null), "Not installed actions");
client.Supported = false;
await vm.RefreshCommand.ExecuteAsync(null);
Check(vm.SupportsInstall && vm.InstallCommand.CanExecute(null), "The explicit install capability remains available when a server reports an unhealthy platform state");
client.Supported = true;
var pendingInstallation = new TaskCompletionSource<FileServiceOperationResultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
client.PendingInstallation = pendingInstallation;
var installation = vm.InstallCommand.ExecuteAsync(null);
Check(vm.IsBusy && vm.StatusText == "file_services.status.installing", "Installation progress replaces the stale not-installed status");
pendingInstallation.SetResult(new FileServiceOperationResultDto(Guid.NewGuid(), true));
await installation;
client.PendingInstallation = null;
client.InstallProblem = FileServiceProblemCodes.RestartRequired;
await vm.InstallCommand.ExecuteAsync(null);
Check(vm.StatusText != FileServiceProblemCodes.RestartRequired, "API problem code localized");
Check(!FileServicesViewModel.RequiresSharePathWarning(@"d:\RelaxKonOSShares\folder", true), "Default Windows subtree needs no warning");
Check(FileServicesViewModel.RequiresSharePathWarning(@"D:\RelaxKonOSSharesOther", true), "Sibling prefix is outside default root");
Check(FileServicesViewModel.RequiresSharePathWarning(@"D:\RelaxKonOSShares\..\private", true), "Traversal cannot skip warning");
Check(!FileServicesViewModel.RequiresSharePathWarning("/srv/relaxkonos-shares/folder", false), "Default Linux subtree needs no warning");
foreach (var linux in new[] { false, true })
{
    var warningClient = new FakeClient { Linux = linux };
    var warningVm = new FileServicesViewModel(warningClient, new Permissions());
    await warningVm.StartAsync();
    warningVm.ShareName = "共享"; warningVm.SharePath = linux ? "/mnt/data" : @"E:\Test";
    var passwordRequests = 0;
    warningVm.RequestHostAdministratorPasswordAsync = () => { passwordRequests++; return Task.FromResult<string?>("test"); };
    var confirmation = new TaskCompletionSource<bool>();
    warningVm.ConfirmSharePathAsync = _ => confirmation.Task;
    var pendingSave = warningVm.SaveShareAsync(false);
    Check(warningVm.IsBusy && passwordRequests == 0 && warningClient.Writes == 0, "Path confirmation precedes password and mutation");
    Check(!await warningVm.SaveShareAsync(false), "Concurrent save cannot bypass warning");
    confirmation.SetResult(false);
    Check(!await pendingSave && warningClient.Writes == 0 && passwordRequests == 0, "Cancel leaves share untouched");
    warningVm.ConfirmSharePathAsync = _ => Task.FromResult(true);
    Check(await warningVm.SaveShareAsync(false) && warningClient.Writes == 1 && passwordRequests == 1, "Confirmed non-default path is shared");
}
Console.WriteLine("Passed: platform discovery, lifecycle refresh, user eligibility, validation, authorization serialization, delete cancellation, install state, API problem localization.");

sealed class Permissions : IAppPermissionScope
{
 public AppPermissionStatus GetStatus(string id) => AppPermissionStatus.Granted;
 public bool IsGranted(string id) => true;
 public Task<AppPermissionStatus> RequestAsync(string id, CancellationToken ct = default) => Task.FromResult(AppPermissionStatus.Granted);
 public Task OpenSettingsAsync() => Task.CompletedTask;
}
sealed class FakeClient : IRemoteFileServicesClient
{
 public bool Linux; public bool Supported = true; public int StatusReads, UserReads, Writes;
 public string? InstallProblem;
 public TaskCompletionSource<FileServiceOperationResultDto>? PendingInstallation;
 public FileServiceRuntimeState State = FileServiceRuntimeState.Running;
 public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct = default) { StatusReads++; return Task.FromResult(new FileServiceStatusDto(FileServiceProtocol.Smb, State, "test", State == FileServiceRuntimeState.Running, true)); }
 public Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct = default) => Task.FromResult(new FileServiceCapabilitiesDto(Supported, Linux, Linux, true, !Linux));
 public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FileShareDto>>([]);
 public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct = default) { UserReads++; return Task.FromResult<IReadOnlyList<FileServiceUserDto>>(Linux ? [new("nanami", false, true)] : []); }
 public Task<FileServiceConnectionInfoDto> GetConnectionAsync(CancellationToken ct = default) => Task.FromResult(new FileServiceConnectionInfoDto("host",445,"\\\\host\\","smb://host/"));
 private Task<FileServiceOperationResultDto> Result() { Writes++; return Task.FromResult(new FileServiceOperationResultDto(Guid.NewGuid(),true)); }
 public Task<FileServiceOperationResultDto> InstallAsync(CancellationToken ct = default) => InstallProblem is { } code
     ? Task.FromException<FileServiceOperationResultDto>(new HttpRequestException(code, null, HttpStatusCode.BadRequest))
     : PendingInstallation?.Task ?? Result();
 public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, CancellationToken ct = default) { State = action == SmbLifecycleAction.Stop ? FileServiceRuntimeState.Stopped : FileServiceRuntimeState.Running; return Result(); }
 public Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest r,CancellationToken ct = default) => Result();
 public Task<FileServiceOperationResultDto> UpdateShareAsync(string id,UpsertFileShareRequest r,CancellationToken ct = default) => Result();
 public Task<FileServiceOperationResultDto> DeleteShareAsync(string id,CancellationToken ct = default) => Result();
 public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username,bool enabled,CancellationToken ct = default) => Result();
 public Task<FileServiceOperationResultDto> SetSambaPasswordAsync(string username,SetSambaPasswordRequest r,CancellationToken ct = default) => Result();
 public Task<bool> ElevateAsync(string password,CancellationToken ct = default) => Task.FromResult(true);
}
