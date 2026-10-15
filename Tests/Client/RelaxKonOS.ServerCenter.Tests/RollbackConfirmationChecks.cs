using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

internal static class RollbackConfirmationChecks
{
    public static async Task RunAsync()
    {
        foreach (var scenario in new[] { "rollback", "rollback-cancel", "rollback-changed", "rollback-missing", "rollback-no-dialog" })
        {
            var directory = Path.Combine(Path.GetTempPath(), "relaxkonos-rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                const string id = "rki-26691ddcd2e74b6080ee10052cddbe11";
                var targets = new HostTargetStore(directory);
                var target = ServerHostTargetRules.Create("192.0.2.9", 22, "alice", null, DateTimeOffset.UtcNow) with { InstallationId = id };
                await targets.UpsertAsync(target);
                var keys = new SshHostKeyTrustStore(directory);
                var factory = new MaintenanceTransportFactory(scenario, id);
                var vm = new ServerCenterViewModel(targets, new ServerCenterConnectionResolver(keys, targets, factory),
                    keys, new SshCredentialStore(directory), new SshDesktopSession(null!, null!), new StubReleaseSource(),
                    new ServerCenterOperationJournal(directory), new LoginLocalizationService(new LocalLanguageStore()));
                await vm.LoadAsync();
                vm.SelectedPlatform = vm.Platforms.Single(p => p.Platform == HostPlatformKind.Linux);
                vm.LastProbeText = "verified";
                vm.HasPreviousVersion = true;
                vm.SshPassword = "test-only";
                var confirmations = 0;
                if (scenario != "rollback-no-dialog") vm.ConfirmRollbackAsync = review =>
                {
                    confirmations++;
                    Check(vm.IsBusy && review.Host == target.DisplayName && review.CurrentVersion == "0.1.3" && review.PreviousVersion == "0.1.2",
                        "Confirmation displays freshly verified versions and host while blocking duplicate operations.");
                    Check(factory.Requests.All(r => r.Kind != ServerDeploymentKind.Rollback), "No rollback request is sent before confirmation.");
                    return Task.FromResult(scenario != "rollback-cancel");
                };
                await vm.RollbackCommand.ExecuteAsync(null);
                Check(factory.Requests.Count(r => r.Kind == ServerDeploymentKind.Rollback) == (scenario == "rollback" ? 1 : 0), scenario + ": only an approved, unchanged version pair can be restored.");
                Check(confirmations == (scenario is "rollback-missing" or "rollback-no-dialog" ? 0 : 1), scenario + ": confirmation is requested once only when both versions are known.");
                Check(vm.HasError == (scenario is "rollback-changed" or "rollback-missing"), scenario + ": changed or missing versions produce a visible failure. " + vm.ErrorMessage);
                Check(!vm.IsBusy, "Restoring or canceling releases the operation lock.");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
