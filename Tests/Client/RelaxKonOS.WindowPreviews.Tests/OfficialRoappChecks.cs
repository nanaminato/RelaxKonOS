using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.WindowManager;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.Protocol.Browser;
using RelaxKonOS.Runtime;
using System.Reflection;
using WindowManager = RelaxKonOS.WindowManager.WindowManager;

internal static class OfficialRoappChecks
{
    public static void Run(string artifact, ShellSettings settings, RelaxKonOS.AppSDK.ISystemLanguage language)
    {
        if (DeveloperProfile.IsDebug) throw new InvalidOperationException("Build this check in Release.");
        var task = RunAsync(Path.GetFullPath(artifact), settings, language);
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new InvalidOperationException("Official package check timed out.");
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string artifact, ShellSettings settings, RelaxKonOS.AppSDK.ISystemLanguage language)
    {
        var applicationId = Path.GetFileNameWithoutExtension(artifact);
        var isNotepad = applicationId == "relaxkonos.notepad";
        var isEventAlerts = applicationId == "relaxkonos.event-alerts";
        var isCodeEditor = applicationId == "relaxkonos.codeeditor";
        var isDocker = applicationId == "relaxkonos.docker";
        var isCertificates = applicationId == "relaxkonos.certificates";
        var isWebServers = applicationId == "relaxkonos.webservers";
        var isGit = applicationId == "relaxkonos.git";
        var isFileServices = applicationId == "relaxkonos.file-services";
        var isFirewall = applicationId == "relaxkonos.firewall";
        var isSquid = applicationId == "relaxkonos.squid";
        var requiresModule = isDocker || isCertificates || isWebServers || isGit || isFirewall || isSquid;
        var isGuardian = applicationId == "relaxkonos.processguardian";
        var isPortForwarding = applicationId == "relaxkonos.port-forwarding";
        var isApplicationDeployments = applicationId == "relaxkonos.application-deployments";
        var isTunnels = applicationId == "relaxkonos.tunnels";
        var interactive = requiresModule || isFileServices || isGuardian || isPortForwarding || isApplicationDeployments || isTunnels;
        var isTaskManager = applicationId == "relaxkonos.taskmanager";
        var isRegistry = applicationId == "relaxkonos.registry";
        var isBrowser = applicationId == "relaxkonos.browser";
        var isTerminal = applicationId == "relaxkonos.terminal";
        var opensFiles = isCodeEditor || applicationId is "relaxkonos.notepad" or "relaxkonos.imageviewer";
        var samplePath = isCodeEditor ? "/files/sample.cs" : isNotepad ? "/files/README" : "/images/test.JFIF";
        var parent = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "preview-qa"));
        var root = Path.Combine(parent, "official-apps-" + Guid.NewGuid().ToString("N"));
        var source = Directory.CreateDirectory(Path.Combine(root, "OfficialApps")).FullName;
        var path = Path.Combine(source, applicationId + ".roapp");
        var drive = new VirtualSystemDrive(Path.Combine(root, "drive"));
        var compatibility = new Compatibility();
        using var services = new ServiceCollection().AddSingleton<IApplicationCompatibilityEvaluator>(compatibility).BuildServiceProvider();
        using var http = new HttpClient();
        using var catalog = new ComponentCatalogSourceStore(Path.Combine(root, "catalog-trust"), "test-desktop");
        var journal = new FileDeploymentJournal(Path.Combine(root, "deployment-journal"));
        var browserData = DispatchProxy.Create<IBrowserData, OfficialBrowserDataFixture>();
        EventAlertsAuthFixture? browserAuth = null;
        try
        {
            var (packages, applications, windows) = CreateRuntime();
            var installer = new AppPackageInstallerService(packages, null!, catalog);
            if (applicationId == "relaxkonos.welcome")
            {
                var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
                ((EventAlertsAuthFixture)(object)auth).State = AuthSessionState.Unauthenticated;
                await PublishCandidateAsync(manifest => manifest["dependencies"] = new JsonObject
                {
                    ["optional"] = new JsonArray(new JsonObject
                    {
                        ["package"] = new JsonObject
                        {
                            ["kind"] = (int)DeploymentPackageKind.ServerModule,
                            ["id"] = "fixture.optional",
                        },
                    }),
                });
                using var review = new AppInstallerViewModel(installer)
                {
                    JointDeployment = new(null!, auth, installer, packages, applications, journal),
                    SelectOptionalAsync = optional =>
                    {
                        Check(optional.Count == 1 && optional[0].Package.Id == "fixture.optional", "The optional module is offered.");
                        return Task.FromResult<IReadOnlyList<DeploymentRequirement>?>([]);
                    },
                    CurrentPackage = await installer.InspectLocalAsync(path),
                };
                var approved = false;
                var confirmations = 0;
                review.ConfirmDeploymentAsync = plan =>
                {
                    confirmations++;
                    Check(plan.OrderedReleases.Length == 1 && plan.OrderedReleases[0].Package.Id == applicationId,
                        "Unchecking the optional module leaves a local application-only plan.");
                    return Task.FromResult(approved);
                };
                await review.InstallCommand.ExecuteAsync(null);
                Check(confirmations == 1 && packages.FindInstalled(applicationId) is null,
                    "Rejecting the local plan must leave the app uninstalled.");
                approved = true;
                await review.InstallCommand.ExecuteAsync(null);
                var result = packages.FindInstalled(applicationId);
                Check(confirmations == 2 && result is not null && File.Exists(Path.Combine(result.InstallationPath, "deployment-receipt.json")),
                    "Approving the local plan must install the app through the coordinator.");
                await packages.UninstallAsync(applicationId);
            }
            File.Copy(artifact, path, overwrite: true);
            File.Copy(artifact + ".sha256", path + ".sha256", overwrite: true);
            await RejectAsync(() => installer.InspectLocalAsync(artifact), "Package outside the configured source");
            await using (var unsigned = File.OpenRead(artifact))
                await RejectAsync(() => packages.InstallAsync(unsigned, launch: false), "Reserved ID via the generic install path");

            var candidate = await installer.InspectLocalAsync(path);
            Check((isEventAlerts || candidate.IconData is { Length: > 0 }) && candidate.ExpectedSha256 is not null,
                "Review should freeze the digest and load any packaged icon.");
            if (isEventAlerts)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerEventAlertsRead],
                    "The official alert package must request only permission to read alerts.");
            if (isDocker)
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage, AppPermissions.ServerComponentsRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.docker"]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.docker" }, Capability: "server.docker" }],
                    "Docker review exposes the required server module, capability and all three permissions.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Docker without joint deployment");
            }
            if (isRegistry)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerRegistryRead, AppPermissions.ServerRegistryWrite],
                    "Registry review exposes separate read and write grants.");
            if (isCertificates)
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerCertificatesRead, AppPermissions.ServerCertificatesManage]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.certificates" }, Capability: "server.certificates" }],
                    "Certificate review exposes the required module and separate read and manage grants.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Certificate Manager without joint deployment");
            }
            if (isWebServers)
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerWebServersRead,
                    AppPermissions.ServerWebServersManage, AppPermissions.ServerComponentsRead,
                    AppPermissions.ServerCertificatesRead, AppPermissions.ServerFilesRead]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.webserver" }, Capability: "server.web-server" }],
                    "Web Server review exposes the required module and scoped app capabilities.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Web Server Manager without joint deployment");
            }
            if (isGit)
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerGitRead, AppPermissions.ServerGitManage,
                    AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite, AppPermissions.ServerComponentsRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.git"]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.git" }, Capability: "server.git" }],
                    "Git review exposes the Git module, capability and scoped grants.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Git without joint deployment");
            }
            if (isFirewall)
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerFirewallRead, AppPermissions.ServerFirewallManage]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.firewall"]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.firewall" }, Capability: "server.firewall" }],
                    "Firewall review exposes its server module and scoped grants.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Firewall without joint deployment");
            }
            if (isApplicationDeployments)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerApplicationDeploymentsRead,
                    AppPermissions.ServerApplicationDeploymentsManage, AppPermissions.ServerFilesRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.application-deployments"]
                    && candidate.Manifest.Dependencies?.Required is null or [],
                    "Application Deployments review exposes the deployment and file grants and required server capability.");
            if (isSquid)
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerSquidRead,
                    AppPermissions.ServerSquidManage, AppPermissions.ServerComponentsRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.gateway-proxy"]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.proxy" }, Capability: "server.gateway-proxy" }],
                    "Squid review exposes its required Proxy module, gateway capability, and scoped grants.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Squid without joint deployment");
            }
            if (isTunnels)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerTunnelsRead,
                    AppPermissions.ServerTunnelsManage, AppPermissions.ServerComponentsRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.tunnels"]
                    && candidate.Manifest.Dependencies?.Required is null or [],
                    "Tunnel Manager review exposes the host capability and scoped grants.");
            if (isPortForwarding)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.DesktopPortForwardingManage]
                    && candidate.Manifest.Dependencies?.Required is null or [],
                    "Port Forwarding review exposes its local SSH management permission.");
            if (isGuardian)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerGuardianRead, AppPermissions.ServerGuardianManage]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.guardian"]
                    && candidate.Manifest.Dependencies?.Required is null or [],
                    "Process Guardian review exposes its Agent capability and scoped grants.");
            if (isFileServices)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerFileServicesRead,
                    AppPermissions.ServerFileServicesManage, AppPermissions.ServerComponentsRead, AppPermissions.ServerFilesRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.file-services"]
                    && candidate.Manifest.Dependencies?.Required is null or [],
                    "File Services review exposes the host capability and scoped grants without a server module dependency.");
            var provisioner = new BundledApplicationProvisioner(packages, installer,
                new JointAppDeploymentService(null!, null!, installer, packages, applications, journal), drive);
            await WriteProfileAsync("interactive");
            await provisioner.RunAsync();
            Check(packages.FindInstalled(applicationId) is null, "Interactive profile entries must not install during startup.");
            var published = (await installer.BrowseOfficialAsync(default)).Releases.Single(release => release.AppId == applicationId);
            var staged = await installer.StageOfficialAsync(published, default);
            Check(staged.ExpectedSha256 == candidate.ExpectedSha256,
                "The existing store must offer the reviewed bundled artifact for interactive deployment.");
            await WriteProfileAsync("offline");
            await File.AppendAllTextAsync(path, "corrupted distribution package");
            await provisioner.RunAsync();
            Check(packages.FindInstalled(applicationId) is null, "A broken bundled package must not prevent startup or register an app.");
            await PublishCandidateAsync(manifest => manifest["serverRequirements"] = new JsonObject
            {
                ["capabilities"] = new JsonArray("git"),
            });
            await provisioner.RunAsync();
            Check(packages.FindInstalled(applicationId) is null, "A profile must defer a server-dependent package for interactive deployment.");
            File.Copy(artifact, path, overwrite: true);
            File.Copy(artifact + ".sha256", path + ".sha256", overwrite: true);
            await provisioner.RunAsync(["--installation-profile=minimal"]);
            await provisioner.RunAsync();
            Check(packages.FindInstalled(applicationId) is null, "The Minimal choice survives a restart.");
            await provisioner.RunAsync(["--installation-profile=standard", $"--exclude-bundled-app={applicationId}"]);
            await provisioner.RunAsync();
            Check(packages.FindInstalled(applicationId) is null, "An excluded application stays uninstalled on restart.");
            compatibility.TaskManagerServerAvailable = !isTaskManager;
            if (interactive)
                await WriteProfileAsync("interactive");
            await provisioner.RunAsync(["--installation-profile=developer"]);
            if (interactive)
                Check(packages.FindInstalled(applicationId) is null, "The server-dependent application stays optional in Developer.");
            var first = interactive
                ? await InstallForLifecycleCheckAsync(installer, await installer.InspectLocalAsync(path))
                : packages.FindInstalled(applicationId) ?? throw new InvalidOperationException("The bundled default did not install.");
            if (!interactive)
                Check(File.Exists(Path.Combine(first.InstallationPath, "deployment-receipt.json")),
                    "Profile provisioning must commit the unified deployment receipt.");
            await provisioner.RunAsync();
            Check(packages.FindInstalled(applicationId) == first, "Provisioning must not reinstall the same application.");
            var id = new AppId(applicationId);
            Check(applications.IsRegistered(id) && applications.SupportsFile(id, samplePath) == opensFiles
                && !applications.IsBuiltIn(id), "Official app should register as a package with its declared file behavior.");

            Check(applications.TextFileOpeners.Any(app => app.Id == id) == (isNotepad || isCodeEditor),
                "Package text fallback must match the manifest.");
            if (isTaskManager)
                Check(!await packages.LaunchAsync(applicationId), "The preinstalled Task Manager must refuse launch without server capabilities.");
            compatibility.TaskManagerServerAvailable = true;
            if (interactive || isTaskManager || isRegistry || isBrowser || isTerminal)
            {
                windows.Attach(new Canvas { Width = 1100, Height = 800 });
                Check(await packages.LaunchAsync(applicationId), "The application must launch from its installed package.");
                for (var attempt = 0; attempt < 50 && windows.Windows.Count == 0; attempt++)
                    await Task.Delay(20);
                var workspace = windows.Windows.Single();
                var workspaceType = applicationId switch
                {
                    "relaxkonos.registry" => "RelaxKonOS.App.Registry.RegistryView",
                    "relaxkonos.taskmanager" => "RelaxKonOS.App.TaskManager.Views.TaskManagerMainView",
                    "relaxkonos.browser" => typeof(TextBlock).FullName,
                    "relaxkonos.terminal" => "RelaxKonOS.App.Terminal.TerminalWorkspaceView",
                    "relaxkonos.certificates" => "RelaxKonOS.App.Certificates.Views.CertificateManagerWorkspace",
                    "relaxkonos.webservers" => "RelaxKonOS.App.WebServers.Views.WebServerManagerWorkspace",
                    "relaxkonos.git" => "RelaxKonOS.App.Git.Views.GitClientWorkspace",
                    "relaxkonos.application-deployments" => "RelaxKonOS.App.ApplicationDeployments.Views.ApplicationDeploymentsWorkspace",
                    "relaxkonos.port-forwarding" => "RelaxKonOS.App.PortForwarding.Views.PortForwardingMainView",
                    "relaxkonos.processguardian" => typeof(DockPanel).FullName,
                    "relaxkonos.tunnels" => "RelaxKonOS.App.Tunnels.Views.TunnelManagerView",
                    "relaxkonos.squid" => "RelaxKonOS.App.Squid.Views.SquidWorkspace",
                    "relaxkonos.firewall" => "RelaxKonOS.App.Firewall.Views.FirewallWorkspace",
                    "relaxkonos.file-services" => "RelaxKonOS.App.FileServices.Views.FileServicesWorkspace",
                    _ => "RelaxKonOS.App.Docker.Views.DockerManagerWorkspace",
                };
                Check(workspace.Info.OwnerAppId == id && workspace.View.Content?.GetType().FullName == workspaceType,
                    "The installed entry point must create its own workspace.");
                if (isApplicationDeployments)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    Check(model.GetType().FullName == "RelaxKonOS.App.ApplicationDeployments.ApplicationDeploymentsViewModel"
                        && model.GetType().GetProperty("CanManage")?.GetValue(model) is false,
                        "The installed deployment workspace honors a read-only grant.");
                }
                if (isSquid)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    Check(model.GetType().GetProperty("CanRead")?.GetValue(model) is true
                        && model.GetType().GetProperty("CanManage")?.GetValue(model) is false,
                        "The installed Squid workspace honors a read-only grant.");
                }
                if (isTunnels)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    Check(model.GetType().GetProperty("CanManage")?.GetValue(model) is false,
                        "The installed Tunnel Manager workspace honors a read-only grant.");
                }
                if (isGuardian)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    Check(model.GetType().FullName == "RelaxKonOS.App.ProcessGuardian.ProcessGuardianViewModel"
                        && model.GetType().GetProperty("OpenCreateWorkloadCommand")?.GetValue(model) is System.Windows.Input.ICommand create
                        && !create.CanExecute(null), "The installed Guardian workspace honors a read-only grant.");
                }
                if (isFirewall)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    var modelType = model.GetType();
                    Check(modelType.GetProperty("IsReadOnly")?.GetValue(model) is true,
                        "The installed Firewall exposes read access and denies management with only the read grant.");
                }

                if (isBrowser)
                {
                    foreach (var (culture, title) in new[]
                    {
                        ("zh-CN", "远程浏览器"), ("en-US", "Remote Browser"), ("ja-JP", "リモート ブラウザー"),
                    })
                    {
                        settings.Language = culture;
                        Dispatcher.UIThread.RunJobs();
                        Check(workspace.Title == title, "The installed Browser title should follow the host language.");
                    }
                }
                windows.Close(workspace);
                if (isTerminal)
                {
                    Check(applications.OpenTerminal("/workspace"), "The installed Terminal handles directory activation.");
                    var terminal = windows.Windows.Single();
                    Check(terminal.Info.OwnerAppId == id && terminal.View.Content?.GetType().FullName == workspaceType,
                        "Opening a directory uses the installed Terminal workspace.");
                    windows.Close(terminal);
                }
                if (isBrowser)
                {
                    settings.Language = "en-US";
                    Dispatcher.UIThread.RunJobs();
                    Check(workspace.Title == "リモート ブラウザー", "A closed Browser window should release its language subscription.");
                    browserAuth!.State = AuthSessionState.Authenticated;
                    Check(await packages.LaunchAsync(applicationId), "The installed Browser should launch after login.");
                    for (var attempt = 0; attempt < 50 && ((OfficialBrowserDataFixture)(object)browserData).SettingsReads == 0; attempt++)
                        await Task.Delay(20);
                    var browserWindow = windows.Windows.Single();
                    Check(browserWindow.View.Content?.GetType().FullName == "RelaxKonOS.App.Browser.Views.BrowserMainView"
                          && ((OfficialBrowserDataFixture)(object)browserData).SettingsReads == 1,
                        "The signed-in package must open its workspace and read settings through the SDK.");
                    windows.Close(browserWindow);
                }
            }

            var installedVersion = Version.Parse(first.Version);
            var updateVersion = new Version(installedVersion.Major, installedVersion.Minor + 1, 0).ToString(3);
            await PublishCandidateAsync(manifest => manifest["version"] = updateVersion);
            var update = await installer.InspectLocalAsync(path);
            Check(update.IsUpdate, "The second release should be offered as an update.");
            await File.AppendAllTextAsync(path, "modified after review");
            await RejectAsync(() => installer.InstallAsync(update), "Changed package after review");
            Check(packages.FindInstalled(applicationId) == first, "The changed bytes should not replace the current version.");

            await PublishCandidateAsync(manifest =>
            {
                manifest["version"] = updateVersion;
                manifest["entryType"] = "Fixture.DoesNotExist";
            });
            await RejectAsync(async () => await InstallForLifecycleCheckAsync(installer, await installer.InspectLocalAsync(path)), "Invalid entry type");
            Check(packages.FindInstalled(applicationId) == first && applications.IsRegistered(id), "A failed candidate must keep the registered version.");

            await PublishCandidateAsync(manifest =>
            {
                manifest["version"] = updateVersion;
                manifest["clientPlatforms"] = new JsonArray("unsupported-test-platform");
            });
            await RejectAsync(async () => await installer.InstallAsync(await installer.InspectLocalAsync(path)), "Incompatible client");

            await PublishCandidateAsync(manifest => manifest["version"] = updateVersion,
                archive => archive.CreateEntry("../outside.dll"));
            await RejectAsync(async () => await installer.InstallAsync(await installer.InspectLocalAsync(path)), "Unsafe archive path");

            await PublishCandidateAsync(manifest => manifest["version"] = updateVersion);
            ManagedWindow? guarded = null;
            if (isNotepad)
            {
                windows.Attach(new Canvas { Width = 1000, Height = 700 });
                guarded = windows.Create(new WindowCreateOptions(id, "Dirty draft", new TextBlock()));
                guarded.CanCloseAsync = () => Task.FromResult(false);
                await RejectAsync(async () => await installer.InstallAsync(await installer.InspectLocalAsync(path)), "Update with an undiscarded draft");
                await RejectAsync(() => packages.UninstallAsync(applicationId), "Uninstall with an undiscarded draft");
                Check(windows.Windows.Contains(guarded) && packages.FindInstalled(applicationId) == first,
                    "Update and uninstall vetoes must preserve the active version and draft window.");
                guarded.CanCloseAsync = () => Task.FromResult(true);
            }
            var upgraded = await InstallForLifecycleCheckAsync(installer, await installer.InspectLocalAsync(path));
            if (guarded is not null)
                Check(!windows.Windows.Contains(guarded), "The accepted update should retire the old window.");
            Check(upgraded.Version == updateVersion && upgraded.InstallationPath != first.InstallationPath && Directory.Exists(first.InstallationPath),
                "The upgrade should leave the previous package version intact.");

            if (applicationId == "relaxkonos.welcome")
            {
                await WriteProfileAsync("offline", "1.2.0");
                await provisioner.RunAsync();
                Check(packages.FindInstalled(applicationId) == upgraded, "An insufficient bundled release must preserve the installed version.");
                await PublishCandidateAsync(manifest => manifest["version"] = "1.2.0");
                await provisioner.RunAsync();
                upgraded = packages.FindInstalled(applicationId) ?? throw new InvalidOperationException("Profile update removed the application.");
                Check(upgraded.Version == "1.2.0" && File.Exists(Path.Combine(upgraded.InstallationPath, "deployment-receipt.json")),
                    "A higher offline profile floor should upgrade an installed application through the coordinator despite the first-install receipt.");
            }

            var receipt = Path.Combine(upgraded.InstallationPath, OfficialDesktopPackages.OriginFileName);
            await File.WriteAllTextAsync(receipt, "\"directDeploy\"");
            var (releaseReader, releaseApplications, _) = CreateRuntime();
            await releaseReader.LoadInstalledAsync();
            Check(!releaseApplications.IsRegistered(id), "Release must reject a Direct Deploy receipt in its VSD.");
            await File.WriteAllTextAsync(receipt, "\"officialDistribution\"");
            await releaseReader.LoadInstalledAsync();
            Check(releaseReader.FindInstalled(applicationId) == upgraded && releaseApplications.IsRegistered(id)
                && releaseApplications.SupportsFile(id, samplePath) == opensFiles
                && !releaseApplications.IsBuiltIn(id), "Release must rediscover the verified package from VSD.");
            Check(await releaseReader.UninstallAsync(applicationId) && !releaseApplications.IsRegistered(id), "Uninstall must unregister the package.");
            await releaseReader.LoadInstalledAsync();
            var releaseInstaller = new AppPackageInstallerService(releaseReader, null!, catalog);
            await new BundledApplicationProvisioner(releaseReader, releaseInstaller,
                new JointAppDeploymentService(null!, null!, releaseInstaller, releaseReader, releaseApplications, journal), drive).RunAsync();
            Check(releaseReader.FindInstalled(applicationId) is null, "Restart must preserve the explicit uninstall.");

            // Retire the original runtime's loaded adapter before removing its DLLs on Windows.
            await packages.UninstallAsync(applicationId);
            Console.WriteLine($"{applicationId}: Official Release .roapp source, integrity, update, failed install, restart and uninstall passed.");
        }
        finally
        {
            // The acceptance runtime can retain loaded DLLs until the process exits on Windows.
            if (Path.GetDirectoryName(root) == parent)
                try { Directory.Delete(root, true); } catch (IOException) { }
        }

        (DeveloperPackageManager, ApplicationManager, WindowManager) CreateRuntime()
        {
            var windows = new WindowManager();
            var apps = new ApplicationManager(windows, services);
            var permissions = new RoappCatalogChecks.Permissions();
            var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
            if (isTerminal)
                ((EventAlertsAuthFixture)(object)auth).State = AuthSessionState.Unauthenticated;
            if (isBrowser)
            {
                browserAuth = (EventAlertsAuthFixture)(object)auth;
                browserAuth.State = AuthSessionState.Unauthenticated;
            }
            var memory = new UsageMemoryStore(Path.Combine(root, "usage-memory.json"));
            var appPermissions = new CertificateCapabilityChecks.Permission(applicationId);
            appPermissions.Granted.Add(AppPermissions.ServerCertificatesRead);
            appPermissions.Granted.Add(AppPermissions.ServerWebServersRead);
            var certificates = isCertificates ? DispatchProxy.Create<IServerCertificates, CertificateCapabilityClient>() : null;
            var webServers = isWebServers ? DispatchProxy.Create<IServerWebServers, CurrentNginxClient>() : null;
            appPermissions.Granted.Add(AppPermissions.ServerGitRead);
            appPermissions.Granted.Add(AppPermissions.ServerGitManage);
            appPermissions.Granted.Add(AppPermissions.ServerFileServicesRead);
            var git = isGit ? DispatchProxy.Create<RelaxKonOS.AppSDK.IServerGit, OfficialGitFixture>() : null;
            var fileServices = isFileServices ? DispatchProxy.Create<RelaxKonOS.AppSDK.IServerFileServices, ShareDialogClient>() : null;
            appPermissions.Granted.Add(AppPermissions.ServerFirewallRead);
            var firewall = isFirewall ? DispatchProxy.Create<IServerFirewall, FirewallHostStub>() : null;
            appPermissions.Granted.Add(AppPermissions.ServerGuardianRead);
            var guardian = isGuardian ? DispatchProxy.Create<IServerGuardian, GuardianCapabilityClient>() : null;
            appPermissions.Granted.Add(AppPermissions.DesktopPortForwardingManage);
            var forwarding = isPortForwarding ? DispatchProxy.Create<IPortForwardingService, PendingForwardService>() : null;
            appPermissions.Granted.Add(AppPermissions.ServerApplicationDeploymentsRead);
            var deployments = isApplicationDeployments
                ? DispatchProxy.Create<IServerApplicationDeployments, DeploymentHostStub>() : null;
            if (deployments is not null) ((DeploymentHostStub)(object)deployments).Seed();
            appPermissions.Granted.Add(AppPermissions.ServerTunnelsRead);
            var tunnels = isTunnels ? DispatchProxy.Create<IServerTunnels, TunnelCapabilityClient>() : null;
            appPermissions.Granted.Add(AppPermissions.ServerSquidRead);
            var squid = isSquid ? new SquidWorkspaceChecks.SquidClientStub() : null;
            var scopedPermissions = isCertificates || isWebServers || isGit || isFileServices || isFirewall
                || isGuardian || isPortForwarding || isApplicationDeployments || isTunnels || isSquid;
            var context = new ExternalAppContextFactory(scopedPermissions ? appPermissions : permissions,
                scopedPermissions ? appPermissions : null!, language, settings, null!, auth, null!,
                windows, null!, new ExplorerClient(http, auth, memory), new SshDesktopSession(null!, settings),
                null!, DispatchProxy.Create<ISettingsNavigation, UnusedPreviewServices>(), null!, null!, null!, memory, null!, null!, null!, null!,
                browserData: isBrowser ? browserData : null, certificates: certificates, webServers: webServers, git: git,
                fileServices: fileServices, firewall: firewall, guardian: guardian, portForwarding: forwarding, applicationDeployments: deployments, tunnels: tunnels, createSquidClient: squid is null ? null : () => squid);
            return (new DeveloperPackageManager(apps, context, windows, permissions, new RoappCatalogChecks.Diagnostics(), drive, source), apps, windows);
        }

        async Task PublishCandidateAsync(Action<JsonObject> editManifest, Action<ZipArchive>? editArchive = null)
        {
            File.Copy(artifact, path, overwrite: true);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!;
                JsonObject manifest;
                using (var input = entry.Open()) manifest = JsonNode.Parse(input)!.AsObject();
                entry.Delete();
                editManifest(manifest);
                using (var output = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                    await output.WriteAsync(manifest.ToJsonString());
                editArchive?.Invoke(zip);
            }
            await using var file = File.OpenRead(path);
            await File.WriteAllTextAsync(path + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
        }

        // This fixture isolates the client package lifecycle from the server module executor.
        Task<DeveloperAppInfo> InstallForLifecycleCheckAsync(AppPackageInstallerService installer, AppPackageCandidate candidate) => requiresModule
            ? installer.InstallAsync(candidate, default, (_, _) => Task.FromResult(Guid.NewGuid()))
            : installer.InstallAsync(candidate);

        Task WriteProfileAsync(string policy, string? minimum = null)
        {
            var entry = new[] { new { appId = applicationId, minimumVersion = minimum ?? (isDocker || isGit ? "0.3.0" : "1.0.0"), installPolicy = policy } };
            return File.WriteAllTextAsync(Path.Combine(source, "installation-profiles.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 2, defaultProfile = "standard",
                    profiles = new { minimal = Array.Empty<object>(), standard = entry, developer = entry },
                }));
        }
    }

    private sealed class Compatibility : IApplicationCompatibilityEvaluator
    {
        public bool TaskManagerServerAvailable { get; set; } = true;
        public ApplicationCompatibilityResult Evaluate(ApplicationManifest manifest) =>
            manifest.Id.Value == "relaxkonos.taskmanager" && manifest.Server.RequiredCapabilities.Count > 0 && !TaskManagerServerAvailable
                ? new(ApplicationCompatibilityStatus.ServerUnavailable)
                : ApplicationCompatibilityResult.Compatible;
    }

    private static async Task RejectAsync(Func<Task> action, string scenario)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return; }
        throw new InvalidOperationException(scenario + " was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class OfficialBrowserDataFixture : DispatchProxy
{
    public int SettingsReads { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case nameof(IBrowserData.ListBookmarksAsync):
                return Task.FromResult<IReadOnlyList<BookmarkDto>>([]);
            case nameof(IBrowserData.ListHistoryAsync):
                return Task.FromResult<IReadOnlyList<HistoryEntryDto>>([]);
            case nameof(IBrowserData.GetSettingsAsync):
                SettingsReads++;
                return Task.FromResult(BrowserSettingsDto.Default);
            default:
                throw new NotSupportedException(method?.Name);
        }
    }
}

public class OfficialGitFixture : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(RelaxKonOS.AppSDK.IServerGit.GetEngineStatusAsync) =>
            Task.FromResult(new RelaxKonOS.Protocol.Git.GitEngineStatusDto(true, string.Empty)),
        nameof(RelaxKonOS.AppSDK.IServerGit.ListRepositoriesAsync) =>
            Task.FromResult<IReadOnlyList<RelaxKonOS.Protocol.Git.GitRepositoryDto>>([]),
        _ => throw new NotSupportedException(method?.Name),
    };
}
