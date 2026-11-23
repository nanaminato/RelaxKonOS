using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Client.Views.Login;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.Protocol.Common;
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
        var basicApps = published["profiles"]!["standard"]!.AsArray()
            .Where(entry => (string?)entry!["installPolicy"] == "offline")
            .Select(entry => (string)entry!["appId"]!)
            .ToArray();
        var parent = Path.Combine(System.AppContext.BaseDirectory, "preview-qa");
        var root = Path.Combine(parent, "desktop-profile-" + Guid.NewGuid().ToString("N"));
        var auth = DispatchProxy.Create<IAuthSession, DesktopDistributionAuth>();
        var session = (DesktopDistributionAuth)(object)auth;
        var server = new ServerDescriptorDto(HostPlatformKind.Windows,
            [ServerCapabilities.Terminal, ServerCapabilities.Metrics, ServerCapabilities.Processes]);
        session.Server = server;
        using var services = new ServiceCollection()
            .AddSingleton<RelaxKonOS.Client.Apps.AppInstaller.AppInstallerApp>()
            .AddSingleton<IApplicationCompatibilityEvaluator>(
                new ApplicationCompatibilityService(auth, null!, null!, new EmptyModuleCatalog(), null!))
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
                if (!applications.Registered.Select(app => app.Id).SequenceEqual([new AppId("relaxkonos.appinstaller")]))
                    throw new InvalidOperationException("Before profile provisioning, the desktop must register only the built-in App Installer.");
                const string interruptedApp = "relaxkonos.notepad";
                var profileSource = source;
                if (profile == "developer")
                {
                    profileSource = Directory.CreateDirectory(Path.Combine(destination, "OfficialApps")).FullName;
                    foreach (var artifact in Directory.EnumerateFiles(source))
                        if (Path.GetFileName(artifact) != interruptedApp + ".roapp")
                            File.Copy(artifact, Path.Combine(profileSource, Path.GetFileName(artifact)));
                }
                var packages = new DeveloperPackageManager(applications, null!, windows, permissions,
                    diagnostics, drive, profileSource);
                var installer = new AppPackageInstallerService(packages, null!, catalog);
                var coordinator = new JointAppDeploymentService(null!, null!, installer, packages, applications,
                    new FileDeploymentJournal(Path.Combine(destination, "journal")));
                var provisioner = new BundledApplicationProvisioner(packages, installer, coordinator, drive);
                var expected = published["profiles"]![profile]!.AsArray()
                    .Where(entry => (string?)entry!["installPolicy"] == "offline")
                    .ToDictionary(entry => (string)entry!["appId"]!, entry => (string)entry!["minimumVersion"]!, StringComparer.Ordinal);
                try
                {
                    if (profile == "standard")
                    {
                        const string excludedApp = "relaxkonos.welcome";
                        await provisioner.RunAsync([], cancellationToken, (profiles, names) =>
                        {
                            var window = new InstallationProfileWindow(profiles, names, new LoginLocalizationService(new LocalLanguageStore()));
                            if (!names["relaxkonos.terminal"].CanProvisionOffline || !names["relaxkonos.taskmanager"].CanProvisionOffline
                                || names["relaxkonos.proxy"].CanProvisionOffline)
                                throw new InvalidOperationException("The preview disagreed with the bundled launch and server module requirements.");
                            var result = window.ShowSelectionAsync();
                            var layout = (DockPanel)window.Content!;
                            var heading = (StackPanel)layout.Children[0];
                            var picker = heading.Children.OfType<ComboBox>().Single();
                            if (profiles.Profiles.Keys.ElementAt(picker.SelectedIndex) != "standard")
                                throw new InvalidOperationException("The first-run window must default to Standard.");
                            var entries = (StackPanel)((ScrollViewer)layout.Children[2]).Content!;
                            var standardIndex = picker.SelectedIndex;
                            var developer = profiles.Profiles["developer"];
                            var welcomeIndex = Array.FindIndex(developer, item => item.AppId == excludedApp);
                            var original = developer[welcomeIndex];
                            developer[welcomeIndex] = original with { MinimumVersion = "99.0.0" };
                            picker.SelectedIndex = Array.IndexOf(profiles.Profiles.Keys.ToArray(), "developer");
                            var outdated = entries.Children.OfType<CheckBox>().Single(item => Equals(item.Tag, excludedApp));
                            if (outdated.Content is not TextBlock { Text: { } versionLabel } || !versionLabel.Contains("99.0.0", StringComparison.Ordinal))
                                throw new InvalidOperationException("The first-run window did not display the unmet offline version requirement.");
                            developer[welcomeIndex] = original;
                            picker.SelectedIndex = standardIndex;
                            var welcome = entries.Children.OfType<CheckBox>().Single(item => Equals(item.Tag, excludedApp));
                            if (welcome.Content is not TextBlock { Text: "Welcome" })
                                throw new InvalidOperationException("The Welcome choice did not show the verified package name.");
                            welcome.IsChecked = false;
                            ((StackPanel)layout.Children[1]).Children.OfType<Button>().Single(item => item.IsDefault)
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            return result;
                        });
                        expected.Remove(excludedApp);
                        await provisioner.RunAsync([], cancellationToken, (_, _) =>
                            throw new InvalidOperationException("The first-run window reopened after saving its selection."));
                        await provisioner.RunAsync(["--installation-profile=minimal"], cancellationToken);
                        await provisioner.RunAsync(["--installation-profile=standard"], cancellationToken);
                        if (packages.FindInstalled(excludedApp) is not null)
                            throw new InvalidOperationException("Returning to Standard installed an excluded application.");
                    }
                    else
                    {
                        await provisioner.RunAsync(profile == "developer"
                            ? ["--installation-profile=developer", "--exclude-bundled-app=relaxkonos.squid"]
                            : [$"--installation-profile={profile}"], cancellationToken);
                        if (profile == "developer")
                        {
                            var receipt = drive.ResolveRootChild($"System/bundled-apps/{interruptedApp}.json");
                            var otherApps = packages.Installed.ToArray();
                            if (File.Exists(receipt) || packages.FindInstalled(interruptedApp) is not null
                                || otherApps.Length != expected.Count - 1)
                                throw new InvalidOperationException("A missing archive prevented provisioning unrelated apps or was recorded as installed.");
                            var archive = interruptedApp + ".roapp";
                            File.Copy(Path.Combine(source, archive), Path.Combine(profileSource, archive));
                            await provisioner.RunAsync([], cancellationToken);
                            if (!File.Exists(receipt) || packages.FindInstalled(interruptedApp) is null
                                || otherApps.Any(app => packages.FindInstalled(app.Id) != app))
                                throw new InvalidOperationException("Provisioning did not recover the restored archive without replacing unaffected apps.");
                            Console.WriteLine("The Developer profile recovered a restored offline archive without reprovisioning other apps.");
                        }
                    }
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
                    foreach (var basicApp in basicApps)
                    {
                        if (profile == "standard" && basicApp == "relaxkonos.welcome") continue;
                        var appId = new AppId(basicApp);
                        if (profile == "minimal")
                        {
                            if (applications.GetManifest(appId) is not null)
                                throw new InvalidOperationException($"Minimal registered {basicApp} before package recovery.");
                            var archive = Path.Combine(source, basicApp + ".roapp");
                            var candidate = await installer.InspectLocalAsync(archive, cancellationToken: cancellationToken);
                            var recovered = await installer.InstallAsync(candidate, cancellationToken);
                            var asset = ReadRepairAsset(archive);
                            var originalPath = Path.Combine(recovered.InstallationPath, asset);
                            var originalBytes = await File.ReadAllBytesAsync(originalPath, cancellationToken);
                            File.Delete(originalPath);
                            var repair = await installer.InspectLocalAsync(archive, cancellationToken: cancellationToken);
                            var repaired = await installer.InstallAsync(repair, cancellationToken);
                            var repairedBytes = await File.ReadAllBytesAsync(Path.Combine(repaired.InstallationPath, asset), cancellationToken);
                            if (recovered.Id != basicApp || repaired.Version != recovered.Version
                                || applications.IsBuiltIn(appId) || applications.GetManifest(appId)?.Version != repaired.Version
                                || !originalBytes.AsSpan().SequenceEqual(repairedBytes))
                                throw new InvalidOperationException($"Minimal could not recover and repair {basicApp} from its bundled archive.");
                            Console.WriteLine($"minimal: installed and repaired {basicApp} through the bundled archive.");
                        }
                        else if (!actual.ContainsKey(basicApp))
                        {
                            throw new InvalidOperationException($"{profile} did not preinstall {basicApp}.");
                        }
                    }
                    if (profile == "standard")
                        await VerifyBasicLaunchRequirementsAsync(applications, session, server, cancellationToken);
                    if (profile == "developer")
                    {
                        using var store = new AppInstallerViewModel(installer) { ProfileChoices = provisioner };
                        await store.RefreshOfficialCommand.ExecuteAsync(null);
                        var proxy = store.OfficialApps.Single(item => item.Id == "relaxkonos.proxy");
                        if (!proxy.SelectedByProfile || store.OfficialApps.Single(item => item.Id == "relaxkonos.squid").SelectedByProfile
                            || store.SelectedOfficialApp is not { SelectedByProfile: true })
                            throw new InvalidOperationException("Developer choices were not carried into the official store.");
                        store.SelectedOfficialApp = proxy;
                        await store.DownloadOfficialCommand.ExecuteAsync(null);
                        if (store.CurrentPackage?.Manifest.Dependencies?.Required is not { Count: > 0 }
                            || packages.FindInstalled(proxy.Id) is not null)
                            throw new InvalidOperationException("The Developer selection did not stage the official Proxy archive for review.");
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
                        var restoredChoices = await provisioner.ReadInteractiveChoicesAsync(cancellationToken);
                        if (restoredChoices.All(entry => entry.AppId != proxy.Id)
                            || restoredChoices.Any(entry => entry.AppId == "relaxkonos.squid"))
                            throw new InvalidOperationException("Returning to Developer erased its saved application exclusions.");
                        Console.WriteLine("Profile switching preserved installed packages and the user's removal.");
                        var selectionPath = drive.ResolveRootChild("System/installation-profile.json");
                        await File.WriteAllTextAsync(selectionPath, "{", cancellationToken);
                        await provisioner.RunAsync([], cancellationToken);
                        if (await File.ReadAllTextAsync(selectionPath, cancellationToken) != "{")
                            throw new InvalidOperationException("Startup overwrote a corrupt selection without an explicit profile.");
                        await provisioner.RunAsync(["--installation-profile=developer", "--exclude-bundled-app=relaxkonos.squid"], cancellationToken);
                        var recoveredChoices = await provisioner.ReadInteractiveChoicesAsync(cancellationToken);
                        if (recoveredChoices.All(entry => entry.AppId != proxy.Id) || recoveredChoices.Any(entry => entry.AppId == "relaxkonos.squid")
                            || packages.FindInstalled(removed) is not null || packages.Installed.Count != actual.Count - 1
                            || actual.Any(pair => pair.Key != removed && packages.FindInstalled(pair.Key) != pair.Value))
                            throw new InvalidOperationException("Explicit profile selection did not restore settings while preserving installed and removed apps.");
                        Console.WriteLine("Explicit profile selection recovered corrupt settings and preserved the user's removal.");
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

    private static async Task VerifyBasicLaunchRequirementsAsync(ApplicationManager applications, DesktopDistributionAuth session,
        ServerDescriptorDto server, CancellationToken cancellationToken)
    {
        foreach (var (appId, capabilities) in new (string, string[])[]
        {
            ("relaxkonos.terminal", [ServerCapabilities.Terminal]),
            ("relaxkonos.taskmanager", [ServerCapabilities.Metrics, ServerCapabilities.Processes]),
        })
        {
            var id = new AppId(appId);
            var manifest = applications.GetManifest(id)!;
            var launches = 0;
            Task Activate() { launches++; return Task.CompletedTask; }

            session.Server = null;
            if (applications.EvaluateCompatibility(manifest).Status != ApplicationCompatibilityStatus.ServerUnavailable
                || await applications.LaunchObservedAsync(id, Activate, cancellationToken))
                throw new InvalidOperationException($"{appId} was launchable without a server.");

            foreach (var capability in capabilities)
            {
                session.Server = server with { Capabilities = server.Capabilities.Where(name => name != capability).ToArray() };
                var result = applications.EvaluateCompatibility(manifest);
                if (result.Status != ApplicationCompatibilityStatus.MissingServerCapability || result.Expected != capability
                    || await applications.LaunchObservedAsync(id, Activate, cancellationToken))
                    throw new InvalidOperationException($"{appId} did not block launch without {capability}.");
            }

            session.Server = server;
            if (launches != 0 || !await applications.LaunchObservedAsync(id, Activate, cancellationToken) || launches != 1)
                throw new InvalidOperationException($"{appId} did not recover launch after capabilities returned.");
            Console.WriteLine($"{appId}: published launch requirements blocked activation until server capabilities returned.");
        }
    }

    private static string ReadRepairAsset(string archive)
    {
        using var package = ZipFile.OpenRead(archive);
        using var manifest = package.GetEntry("manifest.json")!.Open();
        var document = JsonNode.Parse(manifest)!;
        var asset = (string?)document["iconPath"] ?? Path.ChangeExtension((string?)document["entryAssembly"], ".deps.json");
        if (string.IsNullOrEmpty(asset) || Path.IsPathRooted(asset)
            || asset.Split('/', '\\').Any(segment => segment is "" or "." or "..") || package.GetEntry(asset) is null)
            throw new InvalidDataException($"No safe repair asset in {archive}.");
        return asset;
    }
}

public class DesktopDistributionAuth : EventAlertsAuthFixture
{
    public ServerDescriptorDto? Server { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name == "get_CurrentServer" ? Server : base.Invoke(method, args);
}
