using System.Reflection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;

internal static class RepairPackagePickerChecks
{
    public static async Task RunAsync()
    {
        foreach (var scenario in new[] { "select", "cancel", "switch", "fail" })
        {
            var target = ServerHostTargetRules.Create("test-host", 22, "test-user", null, DateTimeOffset.UtcNow);
            var resolver = DispatchProxy.Create<IServerCenterConnectionResolver, DelayedDesktopResolver>();
            var transport = DispatchProxy.Create<IServerCenterSshTransport, DesktopVerificationTransport>();
            var connection = (ServerCenterHostSession)Activator.CreateInstance(typeof(ServerCenterHostSession),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [target, transport], null)!;
            ((DelayedDesktopResolver)(object)resolver).Pending.SetResult(connection);
            var desktop = new SshDesktopSession(null!, null!);
            var vm = new ServerCenterViewModel(null!, resolver, null!, null!, desktop, null!, null!,
                new LoginLocalizationService(new LocalLanguageStore())) { SelectedHost = target, SshPassword = "test-password", RepairPackagePath = "/old.zip" };
            SshDesktopSession? pickerSession = null;
            await vm.BrowseRepairPackageAsync(async browser =>
            {
                pickerSession = browser;
                Check(vm.IsBusy && browser.Endpoint?.Host == target.SshHost && browser.Endpoint.UserName == target.SshUserName
                    && browser.Password == "test-password" && browser.HostKeyFingerprint == connection.ObservedHostKey!.Fingerprint,
                    "Repair file picker uses the reviewed host, account and verified host key.");
                Check(!desktop.IsConnected, "Browsing a repair package does not replace or connect the SSH desktop.");
                await vm.BrowseRepairPackageAsync(_ => throw new Exception("Duplicate picker must be blocked."));
                if (scenario == "fail") throw new IOException("test-only");
                if (scenario == "switch") vm.SelectedHost = ServerHostTargetRules.Create("other-host", 22, "other-user", null, DateTimeOffset.UtcNow);
                return scenario == "cancel" ? null : "/packages/server.zip";
            });
            Check(vm.RepairPackagePath == (scenario == "select" ? "/packages/server.zip" : "/old.zip"), scenario + ": picker updates only the original host after a selection.");
            Check(!vm.IsBusy && pickerSession?.IsConnected == false && ((DesktopVerificationTransport)(object)transport).Disposed,
                "Picker completion releases its credentials, transport and busy state.");
            Check(vm.HasError == (scenario == "fail"), "Picker failures are visible; cancellation is not an error.");
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
