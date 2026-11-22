using System.Text.Json.Nodes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.Runtime;

internal static class DesktopDistributionChecks
{
    public static void Run(string publishDirectory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var task = RunAsync(Path.GetFullPath(publishDirectory), timeout.Token);
        while (!task.IsCompleted && !timeout.IsCancellationRequested)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new TimeoutException("Desktop profile provisioning did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string publishDirectory, CancellationToken cancellationToken)
    {
        var source = Path.Combine(publishDirectory, "OfficialApps");
        var published = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(source, "installation-profiles.json"), cancellationToken))!;
        var parent = Path.Combine(System.AppContext.BaseDirectory, "preview-qa");
        var root = Path.Combine(parent, "desktop-profile-" + Guid.NewGuid().ToString("N"));
        using var services = new ServiceCollection()
            .AddSingleton<RelaxKonOS.Client.Apps.AppInstaller.AppInstallerApp>()
            .AddSingleton<RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterApp>()
            .AddSingleton<RelaxKonOS.Client.Apps.Explorer.ExplorerApp>()
            .AddSingleton<RelaxKonOS.Client.Apps.ServerCenter.ServerCenterApp>()
            .AddSingleton<RelaxKonOS.App.Settings.ISettingsWindow, RelaxKonOS.Client.Apps.Settings.SettingsWindow>()
            .AddSingleton<RelaxKonOS.App.Settings.SettingsApp>()
            .BuildServiceProvider();
        var builtIns = new BuiltInApplicationRegistry(services);
        try
        {
            foreach (var profile in new[] { "minimal", "standard", "developer" })
            {
                var destination = Path.Combine(root, profile);
                var drive = new VirtualSystemDrive(Path.Combine(destination, "drive"));
                using var catalog = new ComponentCatalogSourceStore(Path.Combine(destination, "trust"), "desktop-profile-fixture");
                var windows = new RelaxKonOS.WindowManager.WindowManager();
                var applications = new ApplicationManager(windows, services);
                var permissions = new RoappCatalogChecks.Permissions();
                var diagnostics = new RoappCatalogChecks.Diagnostics();
                var scanner = new ApplicationCatalogScanner(drive, new BuiltInDescriptorSeeder(drive, builtIns), builtIns,
                    services, diagnostics);
                await scanner.ScanAndRegisterBuiltInsAsync(applications, cancellationToken);
                var packages = new DeveloperPackageManager(applications, null!, windows, permissions,
                    diagnostics, drive, source);
                var installer = new AppPackageInstallerService(packages, null!, catalog);
                var coordinator = new JointAppDeploymentService(null!, null!, installer, packages, applications,
                    new FileDeploymentJournal(Path.Combine(destination, "journal")));
                var provisioner = new BundledApplicationProvisioner(packages, installer, coordinator, drive);
                var expected = published["profiles"]![profile]!.AsArray()
                    .Where(entry => (string?)entry!["installPolicy"] == "offline")
                    .ToDictionary(entry => (string)entry!["appId"]!, entry => (string)entry!["minimumVersion"]!, StringComparer.Ordinal);
                try
                {
                    await provisioner.RunAsync(profile == "standard" ? [] : [$"--installation-profile={profile}"], cancellationToken);
                    var actual = packages.Installed.ToDictionary(app => app.Id, StringComparer.Ordinal);
                    var missing = expected.Keys.Except(actual.Keys);
                    var extra = actual.Keys.Except(expected.Keys);
                    if (missing.Any() || extra.Any())
                        throw new InvalidOperationException($"{profile} profile mismatch. Missing: {string.Join(", ", missing)}. Extra: {string.Join(", ", extra)}.");
                    var recoveryApp = new AppId("relaxkonos.appinstaller");
                    if (!applications.IsBuiltIn(recoveryApp) || !applications.SupportsFile(recoveryApp, "recovery.roapp"))
                        throw new InvalidOperationException($"{profile} is missing the built-in package recovery entry.");
                    foreach (var (id, floor) in expected)
                        if (Version.Parse(actual[id].Version) < Version.Parse(floor) || applications.IsBuiltIn(new AppId(id))
                            || applications.GetManifest(new AppId(id))?.Version != actual[id].Version)
                            throw new InvalidOperationException($"{profile} did not register the published package {id}.");
                    Console.WriteLine($"{profile}: provisioned {actual.Count} official packages from the published distribution.");
                    if (profile == "developer")
                    {
                        await provisioner.RunAsync(["--installation-profile=minimal"], cancellationToken);
                        if (packages.Installed.Count != actual.Count
                            || actual.Any(pair => packages.FindInstalled(pair.Key) != pair.Value))
                            throw new InvalidOperationException("Switching to Minimal replaced or removed a registered package.");

                        const string removed = "relaxkonos.welcome";
                        if (!actual.ContainsKey(removed) || !await packages.UninstallAsync(removed))
                            throw new InvalidOperationException("Developer profile did not allow removing Welcome.");
                        await provisioner.RunAsync(["--installation-profile=developer"], cancellationToken);
                        if (packages.FindInstalled(removed) is not null || packages.Installed.Count != actual.Count - 1
                            || actual.Any(pair => pair.Key != removed && packages.FindInstalled(pair.Key) != pair.Value))
                            throw new InvalidOperationException("Returning to Developer restored a removed package or replaced a remaining one.");
                        Console.WriteLine("Profile switching preserved installed packages and the user's removal.");
                    }
                }
                finally
                {
                    foreach (var package in packages.Installed)
                        await packages.UninstallAsync(package.Id);
                }
            }
        }
        finally
        {
            if (Path.GetDirectoryName(root) == parent && Directory.Exists(root))
                try { Directory.Delete(root, true); }
                catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException)
                {
                    // Retired collectible contexts may retain a package DLL until process exit.
                }
        }
    }
}
