using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Protocol.FileServices;

internal static class ReadRecoveryChecks
{
    public static async Task RunAsync()
    {
        var readCurrent = true;
        var switchedReadClient = new FakeClient { Linux = true, BeforeRead = stage => { if (stage == "capabilities") readCurrent = false; } };
        var switchedReadVm = new FileServicesViewModel(switchedReadClient, new Permissions(), () => readCurrent);
        await switchedReadVm.StartAsync();
        Check(switchedReadClient.StatusReads == 0 && switchedReadVm.Capabilities is null
            && !switchedReadVm.FactsAvailable && switchedReadVm.Shares.Count == 0,
            "A login change after capabilities stops subsequent reads and prevents publishing old facts");
        Check(switchedReadVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.session_changed"),
            "Read-time login changes preserve localized ended-login feedback");
        var current = true;
        var scopeClient = new FakeClient { Linux = true };
        var scopeVm = new FileServicesViewModel(scopeClient, new Permissions(), () => current);
        await scopeVm.StartAsync();
        scopeVm.ShareName = "test"; scopeVm.SharePath = "/srv/relaxkonos-shares/test"; scopeVm.AddSharePermission("nanami");
        scopeVm.RequestHostAdministratorCredentialsAsync = _ =>
        {
            current = false;
            return Task.FromResult<HostAdministratorCredentials?>(new("admin", "test"));
        };
        Check(!await scopeVm.SaveShareAsync(false) && scopeClient.Writes == 0 && !scopeVm.CanManage,
            "Changing the window login while elevation is pending prevents a write");
        Check(scopeClient.ElevationCredentials.Count == 0 && scopeVm.ShareName.Length == 0
            && scopeVm.SharePath.Length == 0 && scopeVm.SharePermissions.Count == 0
            && scopeVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.session_changed"),
            "Ended login clears draft and shows localized recovery without sending administrator credentials");
        Check(!scopeVm.CanEditShareDraft && scopeVm.CanCloseShareDraft,
            "An ended login locks old draft fields but retains its close action");
        current = true;
        var passwordClient = new FakeClient { Linux = true };
        var passwordVm = new FileServicesViewModel(passwordClient, new Permissions(), () => current);
        await passwordVm.StartAsync();
        passwordVm.SelectedUser = passwordVm.Users.Single();
        passwordVm.RequestSambaPasswordAsync = () => { current = false; return Task.FromResult<string?>("test-only-password"); };
        await passwordVm.SetSambaPasswordCommand.ExecuteAsync(null);
        Check(passwordClient.Writes == 0 && passwordVm.Users.Count == 0
            && passwordVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.session_changed"),
            "Login change inside the Samba password prompt retains ended-login feedback and sends no password");
        current = true;
        var cancelledClient = new FakeClient { Linux = true };
        var cancelledVm = new FileServicesViewModel(cancelledClient, new Permissions(), () => current);
        await cancelledVm.StartAsync();
        cancelledVm.ShareName = "test"; cancelledVm.SharePath = "/outside/default/root"; cancelledVm.AddSharePermission("nanami");
        cancelledVm.ConfirmSharePathAsync = _ => { current = false; return Task.FromResult(false); };
        Check(!await cancelledVm.SaveShareAsync(false) && cancelledClient.Writes == 0
            && cancelledVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.session_changed"),
            "A cancelled old-login confirmation cannot replace the ended-login feedback");
        Check(scopeVm.Capabilities is null && scopeVm.RuntimeState is null && scopeVm.ConnectionText == "—"
            && scopeVm.VersionText == "—" && !scopeVm.SupportsInstall && !scopeVm.SupportsSambaCredentials,
            "Ended login removes platform, connection and lifecycle facts from the old window");
        current = true;
        var lateClient = new FakeClient { Linux = true, RejectWrite = System.Net.HttpStatusCode.Forbidden, BeforeWriteReceipt = () => current = false };
        var lateVm = new FileServicesViewModel(lateClient, new Permissions(), () => current)
        { RequestHostAdministratorCredentialsAsync = _ => Task.FromResult<HostAdministratorCredentials?>(new("admin", "test")) };
        await lateVm.StartAsync();
        lateVm.ShareName = "test"; lateVm.SharePath = "/srv/relaxkonos-shares/test"; lateVm.AddSharePermission("nanami");
        Check(!await lateVm.SaveShareAsync(false) && lateVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.session_changed"),
            "An old login's late HTTP refusal cannot replace the ended-login feedback");
        current = true;
        var lateSuccessClient = new FakeClient { Linux = true, BeforeWriteReceipt = () => current = false };
        var lateSuccessVm = new FileServicesViewModel(lateSuccessClient, new Permissions(), () => current)
        { RequestHostAdministratorCredentialsAsync = _ => Task.FromResult<HostAdministratorCredentials?>(new("admin", "test")) };
        await lateSuccessVm.StartAsync();
        lateSuccessVm.ShareName = "old draft"; lateSuccessVm.SharePath = "/srv/relaxkonos-shares/test"; lateSuccessVm.AddSharePermission("nanami");
        var readsBeforeReceipt = lateSuccessClient.StatusReads;
        Check(!await lateSuccessVm.SaveShareAsync(false) && lateSuccessClient.Writes == 1
            && lateSuccessClient.StatusReads == readsBeforeReceipt && !lateSuccessVm.FactsAvailable
            && lateSuccessVm.ShareName.Length == 0 && !lateSuccessVm.CanManage
            && lateSuccessVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.session_changed"),
            "An ended login's successful receipt cannot refresh, restore draft or close the old editor as successful");
        foreach (var stage in new[] { "capabilities", "status", "shares", "users", "connection" })
        {
            var share = new FileShareDto("original", "original", "/srv/relaxkonos-shares/original", null, false, true, false, [], true);
            var client = new FakeClient { Linux = true, ShareData = [share] };
            var vm = new FileServicesViewModel(client, new Permissions(), () => true);
            await vm.StartAsync();
            vm.SelectedShare = share;
            vm.SelectedUser = vm.Users.Single();
            vm.ShareName = "unsaved draft";
            var selectedUser = vm.SelectedUser;
            client.ShareData = [share with { Name = "replacement" }];
            client.FailRead = stage;
            await vm.RefreshCommand.ExecuteAsync(null);
            Check(!vm.FactsAvailable && !vm.CanManage && !vm.NewShareCommand.CanExecute(null)
                && !vm.DeleteShareCommand.CanExecute(null) && !vm.StopCommand.CanExecute(null), stage + " stale facts gate");
            Check(vm.Shares.Single() == share && vm.SelectedShare == share && vm.SelectedUser == selectedUser
                && vm.Users.Count == 1 && vm.ShareName == "unsaved draft", stage + " preserves display and draft");
            Check(vm.RefreshCommand.CanExecute(null) && !await vm.SaveShareAsync(true) && client.Writes == 0,
                stage + " recovery remains available, direct save blocked");
            client.FailRead = null;
            await vm.RefreshCommand.ExecuteAsync(null);
            Check(vm.FactsAvailable && vm.CanManage && vm.Shares.Single().Name == "replacement"
                && vm.SelectedShare?.Id == share.Id && vm.ShareName == "unsaved draft", stage + " complete recovery");
        }
        var failed = new FakeClient { FailRead = "connection" };
        var initial = new FileServicesViewModel(failed, new Permissions(), () => true);
        await initial.StartAsync();
        Check(!initial.FactsAvailable && initial.Capabilities is null && initial.RuntimeState is null,
            "Initial partial read cannot publish facts");
        Console.WriteLine("PASS: SMB read failures at all five stages preserve display/draft, block mutations and recover.");
        var writeClient = new FakeClient { Linux = true };
        var writeVm = new FileServicesViewModel(writeClient, new Permissions(), () => true)
        { RequestHostAdministratorCredentialsAsync = _ => Task.FromResult<RelaxKonOS.Client.Apps.FileServices.HostAdministratorCredentials?>(new("admin", "test")) };
        await writeVm.StartAsync();
        writeVm.ShareName = "test"; writeVm.SharePath = "/srv/relaxkonos-shares/test";
        writeVm.AddSharePermission("nanami");
        writeClient.FailRead = "connection";
        Check(await writeVm.SaveShareAsync(false) && writeClient.Writes == 1 && !writeVm.FactsAvailable,
            "A successful write stays successful when its refresh fails");
        Check(writeVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.success_refresh_failed"),
            "Successful receipt has explicit refresh-only recovery feedback");
        Check(!await writeVm.SaveShareAsync(false) && writeClient.Writes == 1, "Stale facts prevent repeating the successful write");
        writeClient.FailRead = null;
        await writeVm.RefreshCommand.ExecuteAsync(null);
        writeClient.LoseWriteReceipt = true;
        Check(!await writeVm.SaveShareAsync(false) && !writeVm.FactsAvailable && writeClient.Writes == 2,
            "Missing write receipt invalidates the prior facts");
        Check(writeVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.operation_unknown")
            && !await writeVm.SaveShareAsync(false) && writeClient.Writes == 2,
            "Unknown result has verification guidance and blocks immediate duplicate submission");
        await writeVm.RefreshCommand.ExecuteAsync(null);
        writeClient.LoseWriteReceipt = false;
        writeClient.RejectWrite = System.Net.HttpStatusCode.Forbidden;
        Check(!await writeVm.SaveShareAsync(false) && writeVm.StatusText.Resolve().Contains("403")
            && writeVm.StatusText.Resolve() != RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.operation_unknown"),
            "An explicit HTTP refusal is distinguished from a missing receipt");
        foreach (var status in new[] { System.Net.HttpStatusCode.RequestTimeout, System.Net.HttpStatusCode.InternalServerError })
        {
            await writeVm.RefreshCommand.ExecuteAsync(null);
            writeClient.RejectWrite = status;
            var before = writeClient.Writes;
            Check(!await writeVm.SaveShareAsync(false) && !writeVm.FactsAvailable
                && writeVm.StatusText.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("file_services.operation_unknown"),
                "HTTP " + (int)status + " retains uncertain write outcome");
            Check(!await writeVm.SaveShareAsync(false) && writeClient.Writes == before + 1,
                "HTTP " + (int)status + " cannot immediately resubmit against stale facts");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
