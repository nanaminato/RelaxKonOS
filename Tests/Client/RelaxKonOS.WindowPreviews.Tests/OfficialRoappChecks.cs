using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using RelaxKonOS.WindowManager;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Apps.AppInstaller;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.Protocol.Browser;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Developer;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Runtime;
using System.Reflection;
using WindowManager = RelaxKonOS.WindowManager.WindowManager;

internal static class OfficialRoappChecks
{
    private static async Task SubmitInstalledDeploymentAsync(WindowManager windows, object model)
    {
        var modelType = model.GetType();
        var deploy = (System.Windows.Input.ICommand)modelType.GetProperty("DeployRevisionCommand")!.GetValue(model)!;
        var rows = (System.Collections.IEnumerable)modelType.GetProperty("Applications")!.GetValue(model)!;
        object? row = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            row = rows.Cast<object>().SingleOrDefault();
            if (row is not null && modelType.GetProperty("CanActOnSelection")!.GetValue(model) is true) break;
            await Task.Delay(50);
        }
        var marker = (string?)row?.GetType().GetProperty("Name")!.GetValue(row);
        Check(marker is not null && System.Text.RegularExpressions.Regex.IsMatch(marker, @"\Ahttp-fixture-[0-9a-f]{12}\z")
            && modelType.GetProperty("CanActOnSelection")!.GetValue(model) is true, "The installed window did not load the isolated deployment definition.");
        deploy.Execute(null);
        Control? dialog = null;
        for (var attempt = 0; attempt < 100 && dialog is null; attempt++)
        {
            dialog = windows.Windows.Select(window => window.View.Content).OfType<Control>().SingleOrDefault(
                view => view.GetType().FullName == "RelaxKonOS.App.ApplicationDeployments.Views.DeploymentWizardView");
            if (dialog is null) await Task.Delay(50);
        }
        Check(dialog?.DataContext is not null, "The installed workspace did not open its deployment wizard.");
        var wizard = dialog!.DataContext!;
        var wizardType = wizard.GetType();
        wizardType.GetProperty("ImageReference")!.SetValue(wizard, "busybox:1.37");
        wizardType.GetProperty("ArgumentsText")!.SetValue(wizard, $"sh\n-ec\nmkdir -p /tmp/site; echo {marker} > /tmp/site/ready; exec httpd -f -p 8080 -h /tmp/site");
        ((System.Windows.Input.ICommand)wizardType.GetProperty("SubmitCommand")!.GetValue(wizard)!).Execute(null);
        for (var attempt = 0; attempt < 300 && wizardType.GetProperty("IsBusy")!.GetValue(wizard) is true; attempt++)
            await Task.Delay(100);
        var operation = wizardType.GetProperty("Operation")!.GetValue(wizard) as DeploymentOperationDto;
        Check(wizardType.GetProperty("IsFinished")!.GetValue(wizard) is true && operation?.State == DeploymentOperationState.Succeeded,
            $"The installed wizard did not receive a success receipt. State: {operation?.State}, error: {wizardType.GetProperty("ErrorText")!.GetValue(wizard)}");
        ((System.Windows.Input.ICommand)wizardType.GetProperty("CloseCommand")!.GetValue(wizard)!).Execute(null);
        for (var attempt = 0; attempt < 100 && windows.Windows.Count > 1; attempt++) await Task.Delay(50);
        Check(windows.Windows.Count == 1, "The installed deployment wizard did not close after completion.");
        Console.WriteLine("The installed Application Deployments wizard displayed a completed deployment receipt.");
    }

    private static async Task<IReadOnlyList<DeploymentRequirement>?> DeselectOptionalInDialogAsync(
        IReadOnlyList<DeploymentRequirement> requirements)
    {
        var (windows, owner) = OpenReviewOwner();
        try
        {
            Control? view = null;
            var result = windows.ShowDialogAsync<IReadOnlyList<DeploymentRequirement>>(owner, "Optional",
                dialog => view = AppInstallerApp.CreateOptionalSelection(dialog, requirements));
            var choices = view!.GetLogicalDescendants().OfType<ItemsControl>().Single().ItemsSource!
                .Cast<CheckBox>().ToArray();
            Check(choices.Length == requirements.Count && choices.All(choice => choice.IsChecked == true),
                "The production optional dependency dialog checks each option by default.");
            foreach (var choice in choices) choice.IsChecked = false;
            ClickReview(view, "common.ok");
            return await result;
        }
        finally { windows.ForceClose(owner); }
    }

    private static async Task<bool> ConfirmReadOnlyDeploymentAsync(DeploymentPlan plan, bool approved)
    {
        var (windows, owner) = OpenReviewOwner();
        try
        {
            var required = new DeploymentRelease(new(DeploymentPackageKind.ServerModule, "fixture.required"),
                "2.0.0", "fixture-artifact", false, [], System.Collections.Immutable.ImmutableDictionary<string, string>.Empty)
                with { CurrentVersion = "1.0.0" };
            var satisfied = required with
            {
                Package = new(DeploymentPackageKind.ServerModule, "fixture.satisfied"),
                Satisfied = true,
                CurrentVersion = "2.0.0"
            };
            var display = plan with { OrderedReleases = [required, satisfied, .. plan.OrderedReleases] };
            Control? view = null;
            var result = windows.ShowDialogAsync<bool>(owner, "Review",
                dialog => view = AppInstallerApp.CreateDeploymentReview(dialog, display));
            var descendants = view!.GetLogicalDescendants().ToArray();
            Check(descendants.OfType<TextBlock>().Any(text => text.Text?.Contains(required.Package.Id, StringComparison.Ordinal) == true)
                && !descendants.OfType<CheckBox>().Any(), "The production review lists required modules without a deselection control.");
            var messages = descendants.OfType<TextBlock>().Select(text => text.Text).ToArray();
            Check(messages.Contains(LocalizedText.Format("app_installer.deployment.version_change",
                    required.Package.Id, required.CurrentVersion!, required.Version))
                && messages.Contains(LocalizedText.Format("app_installer.deployment.satisfied",
                    satisfied.Package.Id, satisfied.Version)),
                "The production review shows the exact upgrade and identifies the reused module.");
            ClickReview(view, approved ? "common.install" : "common.cancel");
            return await result;
        }
        finally { windows.ForceClose(owner); }
    }

    private static (WindowManager Windows, ManagedWindow Owner) OpenReviewOwner()
    {
        var windows = new WindowManager();
        windows.Attach(new Canvas { Width = 960, Height = 720 });
        var owner = windows.Create(new WindowCreateOptions(new("fixture.deployment"), "Test", new TextBlock(),
            new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
        return (windows, owner);
    }

    private static void ClickReview(Control view, string label)
    {
        var text = RelaxKonOS.Client.Localization.LocalizedText.Get(label);
        view.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, text))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private sealed class OfficialRecoveryTransport(Func<HttpRequestMessage, HttpContent> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = respond(request),
            });
        }
    }

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
        var isProxyManager = applicationId == "relaxkonos.proxy";
        var isProxyServer = applicationId == "relaxkonos.proxy-server";
        var isDatabase = applicationId == "relaxkonos.database";
        var isTailscale = applicationId == "relaxkonos.tailscale";
        var isTunnels = applicationId == "relaxkonos.tunnels";
        var requiresModule = isTunnels || isDocker || isCertificates || isWebServers || isGit || isFirewall || isSquid || isProxyServer || isProxyManager || isTailscale || isDatabase;
        var isGuardian = applicationId == "relaxkonos.processguardian";
        var isPortForwarding = applicationId == "relaxkonos.port-forwarding";
        var isApplicationDeployments = applicationId == "relaxkonos.application-deployments";
        var interactive = requiresModule || isFileServices || isGuardian || isPortForwarding || isApplicationDeployments;
        var isTaskManager = applicationId == "relaxkonos.taskmanager";
        var isRegistry = applicationId == "relaxkonos.registry";
        var isBrowser = applicationId == "relaxkonos.browser";
        var isTerminal = applicationId == "relaxkonos.terminal";
        var isSshFiles = applicationId == "relaxkonos.ssh-files";
        var sftpPort = isSshFiles ? Environment.GetEnvironmentVariable("RK_TEST_SFTP_PORT") : null;
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
        var alertPort = isEventAlerts ? Environment.GetEnvironmentVariable("RK_TEST_EVENT_ALERTS_PORT") : null;
        Uri? alertEndpoint = alertPort is null ? null : new Uri($"http://127.0.0.1:{int.Parse(alertPort)}/");
        var performancePort = isTaskManager ? Environment.GetEnvironmentVariable("RK_TEST_PERFORMANCE_PORT") : null;
        Uri? performanceEndpoint = performancePort is null ? null : new Uri($"http://127.0.0.1:{int.Parse(performancePort)}/");
        var registryPort = isRegistry ? Environment.GetEnvironmentVariable("RK_TEST_REGISTRY_PORT") : null;
        Uri? registryEndpoint = registryPort is null ? null : new Uri($"http://127.0.0.1:{int.Parse(registryPort)}/");
        var deploymentsPort = isApplicationDeployments ? Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_PORT") : null;
        Uri? deploymentsEndpoint = deploymentsPort is null ? null : new Uri($"http://127.0.0.1:{int.Parse(deploymentsPort)}/");
        var submitDeployment = deploymentsEndpoint is not null && Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_SUBMIT") == "1";
        if (alertEndpoint is not null || performanceEndpoint is not null) http.DefaultRequestHeaders.Add("X-Test-Role", "observer");
        if (deploymentsEndpoint is not null) http.DefaultRequestHeaders.Add("X-Test-Role", submitDeployment ? "controller" : "reader");
        using var catalog = new ComponentCatalogSourceStore(Path.Combine(root, "catalog-trust"), "test-desktop");
        var journal = new FileDeploymentJournal(Path.Combine(root, "deployment-journal"));
        var browserData = DispatchProxy.Create<IBrowserData, OfficialBrowserDataFixture>();
        EventAlertsAuthFixture? browserAuth = null;
        DatabasePackageAuthFixture? databaseAuth = null;
        try
        {
            var (packages, applications, windows, sshFixture) = CreateRuntime();
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
                        return DeselectOptionalInDialogAsync(optional);
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
                    return ConfirmReadOnlyDeploymentAsync(plan, approved);
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
            Check((isEventAlerts || isSshFiles || candidate.IconData is { Length: > 0 }) && candidate.ExpectedSha256 is not null,
                "Review should freeze the digest and load any packaged icon.");
            if (isSshFiles)
            {
                var resumeJournal = new FileDeploymentJournal(Path.Combine(root, "resume-package-digest"));
                var deployment = new JointAppDeploymentService(null!, null!, installer, packages, applications, resumeJournal);
                var review = await deployment.PrepareAsync(candidate, [], CancellationToken.None);
                await using (var lease = await resumeJournal.OpenAsync(review.Plan, CancellationToken.None))
                    await lease.SaveAsync(new DeploymentCheckpoint(0, false), CancellationToken.None);
                var unhashed = candidate with { ExpectedSha256 = null };
                var resumed = await deployment.FindPendingAsync(unhashed, CancellationToken.None, localOnly: true);
                Check(resumed?.Plan.Id == review.Plan.Id && resumed?.Candidate.ExpectedSha256 == candidate.ExpectedSha256,
                    "Selecting the same bytes without a catalog hash must recover the pending plan and freeze its digest.");
                var otherPath = Path.Combine(root, "different-package-bytes.roapp");
                File.Copy(candidate.LocalPath, otherPath);
                await using (var other = new FileStream(otherPath, FileMode.Append))
                    other.WriteByte(0);
                Check(await deployment.FindPendingAsync(unhashed with { LocalPath = otherPath },
                    CancellationToken.None, localOnly: true) is null,
                    "Archive bytes that differ from the reviewed digest must not resume a pending plan.");
                var reopened = new JointAppDeploymentService(null!, null!, installer, packages, applications,
                    new FileDeploymentJournal(Path.Combine(root, "resume-package-digest")));
                var discovered = await reopened.RecoverArchivesAsync([], CancellationToken.None);
                Check(discovered is [{ IsRecoveredDeployment: true }] && discovered[0].LocalPath == candidate.LocalPath,
                    "Reopening the recovery journal must discover the original archive without choosing it again.");
                await File.AppendAllTextAsync(path, "changed after deployment review");
                Check((await reopened.RecoverArchivesAsync([], CancellationToken.None)).Count == 0,
                    "Recovery discovery must exclude the original path when its archive no longer matches.");
                File.Copy(artifact, path, overwrite: true);
            }
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
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.git" }, Capability: "server.git",
                            ContractVersion: RelaxKonOS.Protocol.Git.GitApiRoutes.CapabilityContractVersion }],
                    "Git review exposes the versioned Git capability and scoped grants.");
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
            if (isDatabase)
            {
                Check(candidate.Manifest.Dependencies?.Required is
                    [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.database" }, Capability: ServerCapabilities.Database }],
                    "Databases review exposes its server module and capability.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Databases without joint deployment");
            }
            if (isApplicationDeployments)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerApplicationDeploymentsRead,
                    AppPermissions.ServerApplicationDeploymentsManage, AppPermissions.ServerFilesRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.application-deployments"]
                    && candidate.Manifest.Dependencies?.Required is null or [],
                    "Application Deployments review exposes the deployment and file grants and required server capability.");
            if (isProxyManager)
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage,
                    AppPermissions.ServerProxyTunManage, AppPermissions.ServerComponentsRead, AppPermissions.ServerMetricsRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.proxy"]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.proxy" }, Capability: "server.proxy" }],
                    "Proxy Manager review exposes the shared Proxy module and independent TUN grant.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Proxy Manager without joint deployment");
            }
            if (isProxyServer)
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerProxyServerRead, AppPermissions.ServerProxyServerManage,
                    AppPermissions.ServerProxyServerExport, AppPermissions.ServerComponentsRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.proxy-server"]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.proxy" }, Capability: "server.proxy-server" }],
                    "Proxy Server review exposes the shared Proxy module and independent export grant.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Proxy Server without joint deployment");
            }
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
            {
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerTunnelsRead,
                    AppPermissions.ServerTunnelsManage, AppPermissions.ServerComponentsRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.tunnels"]
                    && candidate.Manifest.Dependencies?.Required is
                        [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.tunnels" }, Capability: "server.tunnels" }],
                    "Tunnel Manager review exposes its required module, capability and scoped grants.");
                await RejectAsync(() => installer.InstallAsync(candidate), "Tunnel Manager without joint deployment");
            }
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
            await WriteProfileAsync("offline");
            File.Delete(path);
            var reviewedMissingArchive = false;
            await provisioner.RunAsync(chooseProfile: (profiles, names) =>
            {
                reviewedMissingArchive = true;
                Check(!names.ContainsKey(applicationId), "A missing bundled archive must not supply a display name.");
                var language = new LoginLocalizationService(new LocalLanguageStore());
                var window = new RelaxKonOS.Client.Views.Login.InstallationProfileWindow(profiles, names, language);
                var selection = window.ShowSelectionAsync();
                var layout = (DockPanel)window.Content!;
                var entries = (StackPanel)((ScrollViewer)layout.Children[2]).Content!;
                var row = entries.Children.OfType<CheckBox>().Single(item => Equals(item.Tag, applicationId));
                var expected = string.Format(language.Get("installation_profile.archive_unavailable",
                    "{0} · bundled archive unavailable"), applicationId);
                Check(row.Content is TextBlock label && label.Text == expected, "The first-run window identifies the unavailable archive.");
                ((StackPanel)layout.Children[1]).Children.OfType<Button>().Single(item => item.IsDefault)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return selection;
            });
            Check(reviewedMissingArchive && packages.FindInstalled(applicationId) is null,
                "First-run review proceeds when an offline profile archive is missing.");
            File.Copy(artifact, path);
            await WriteProfileAsync("interactive");
            await provisioner.RunAsync();
            Check(packages.FindInstalled(applicationId) is null, "Interactive profile entries must not install during startup.");
            var published = (await installer.BrowseOfficialAsync(default)).Releases.Single(release => release.AppId == applicationId);
            var staged = await installer.StageOfficialAsync(published, default);
            Check(staged.ExpectedSha256 == candidate.ExpectedSha256,
                "The existing store must offer the reviewed bundled artifact for interactive deployment.");
            if (isSshFiles)
            {
                var catalogJournal = new FileDeploymentJournal(Path.Combine(root, "resume-catalog-release"));
                var deployment = new JointAppDeploymentService(null!, null!, installer, packages, applications, catalogJournal);
                var review = await deployment.PrepareAsync(staged, [], CancellationToken.None);
                await using (var lease = await catalogJournal.OpenAsync(review.Plan, CancellationToken.None))
                    await lease.SaveAsync(new DeploymentCheckpoint(0, false), CancellationToken.None);
                var reopened = new JointAppDeploymentService(null!, null!, installer, packages, applications,
                    new FileDeploymentJournal(Path.Combine(root, "resume-catalog-release")));
                Check((await reopened.RecoverArchivesAsync([published with { Sha256 = new string('0', 64) }],
                    CancellationToken.None)).Count == 0, "A different catalog digest must not recover a deployment.");
                var recovered = await reopened.RecoverArchivesAsync([published], CancellationToken.None);
                Check(recovered is [{ IsRecoveredDeployment: true }] && recovered[0].OfficialRelease == published
                    && recovered[0].ExpectedSha256 == candidate.ExpectedSha256,
                    "The current catalog must recover the exact reviewed release without an archive path.");
                var unauthorized = candidate with
                {
                    CatalogComponentId = "fixture.component",
                    CatalogAuthorization = new ComponentCatalogInstallAuthorization("fixture.missing-source", "fixture.publisher",
                        1, "fixture-policy", 1, "fixture.release", candidate.ExpectedSha256!, "fixture-store"),
                };
                var componentReview = await deployment.PrepareAsync(unauthorized, [], CancellationToken.None);
                Check(componentReview.Plan.ClientBinding is
                    {
                        ArchivePath: null,
                        RecoverFromOfficialCatalog: false,
                        ComponentRelease: { SourceId: "fixture.missing-source", ComponentId: "fixture.component" },
                    }, "Component recovery must retain provenance without retaining the old authorization or using the local path.");
                var componentOnlyPath = Path.Combine(root, "resume-removed-component-source");
                var componentOnlyJournal = new FileDeploymentJournal(componentOnlyPath);
                await using (var componentLease = await componentOnlyJournal.OpenAsync(componentReview.Plan, CancellationToken.None))
                    await componentLease.SaveAsync(new DeploymentCheckpoint(0, false), CancellationToken.None);
                var componentRecovery = new JointAppDeploymentService(null!, null!, installer, packages, applications,
                    new FileDeploymentJournal(componentOnlyPath));
                Check((await componentRecovery.RecoverArchivesAsync([published], CancellationToken.None)).Count == 0,
                    "A removed component source must prevent recovery even when a bundled archive has the same digest.");
                Check(await componentRecovery.FindPendingAsync(candidate, CancellationToken.None, localOnly: true) is null,
                    "A bundled archive cannot bypass the recorded component source even when the digest matches.");
                await CheckOnlineRecoveryAsync(published);
            }
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
            await provisioner.RunAsync(["--installation-profile=standard"]);
            Check(packages.FindInstalled(applicationId) is null, "Explicitly selecting the same profile preserves its exclusions.");
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
            if (isDatabase)
            {
                Check((await installer.InspectLocalAsync(path)).Manifest.ServerRequirements?.RequiredCapabilities is [ServerCapabilities.Database],
                    "The official Databases package must declare the database server capability.");
                databaseAuth!.DatabaseAvailable = false;
                Check(!await packages.LaunchAsync(applicationId) && windows.Windows.Count == 0,
                    "Databases must refuse launch when the connected server has disabled the module.");
                databaseAuth.DatabaseAvailable = true;
            }
            compatibility.TaskManagerServerAvailable = true;
            if (alertEndpoint is not null)
            {
                windows.Attach(new Canvas { Width = 1100, Height = 800 });
                Check(await packages.LaunchAsync(applicationId), "The installed Event & Alert Center opens from its archive.");
                var alertWindow = windows.Windows.Single();
                var alertList = ((DockPanel)alertWindow.View.Content!).Children.OfType<ListBox>().Single();
                for (var attempt = 0; attempt < 100 && alertList.ItemsSource is null; attempt++)
                    await Task.Delay(50);
                Check(alertList.ItemsSource?.Cast<string>().SingleOrDefault()?.Contains("deployment.build_failed", StringComparison.Ordinal) == true,
                    "The installed window displays the server's durable alert through the read capability.");
                windows.ForceClose(alertWindow);
                Console.WriteLine("The installed Event & Alert Center displayed a durable alert over loopback HTTP.");
            }
            if (interactive || isTaskManager || isRegistry || isBrowser || isTerminal || isSshFiles)
            {
                windows.Attach(new Canvas { Width = 1100, Height = 800 });
                Check(await packages.LaunchAsync(applicationId), "The application must launch from its installed package.");
                for (var attempt = 0; attempt < 50 && windows.Windows.Count == 0; attempt++)
                    await Task.Delay(20);
                var workspace = windows.Windows.Single();
                if (registryEndpoint is not null)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    var keys = (System.Collections.IEnumerable)model.GetType().GetProperty("Keys")!.GetValue(model)!;
                    for (var attempt = 0; attempt < 100 && !keys.Cast<object>().Any(); attempt++) await Task.Delay(50);
                    Check(keys.Cast<object>().Any(), "The installed Registry loaded the server hive.");
                    model.GetType().GetProperty("NavigationPathInput")!.SetValue(model, @"HKEY_CURRENT_USER\Workspace\Desktop");
                    ((System.Windows.Input.ICommand)model.GetType().GetProperty("NavigateCommand")!.GetValue(model)!).Execute(null);
                    var entries = (System.Collections.IEnumerable)model.GetType().GetProperty("Entries")!.GetValue(model)!;
                    object? FindTimeFormat(string value) => entries.Cast<object>().SingleOrDefault(row => row.GetType().GetProperty("Source")!.GetValue(row)
                        is RelaxKonOS.Protocol.Registry.RegistryEntryDto { Name: "timeFormat" } entry && entry.DesiredValue.GetString() == value);
                    for (var attempt = 0; attempt < 100 && FindTimeFormat("12h") is null; attempt++) await Task.Delay(50);
                    var savedTimeFormat = FindTimeFormat("12h");
                    Check(savedTimeFormat is not null, "The installed Registry displays the saved workspace time format through the SDK.");
                    model.GetType().GetProperty("SelectedEntry")!.SetValue(model, savedTimeFormat);
                    ((System.Windows.Input.ICommand)model.GetType().GetProperty("ResetSelectedCommand")!.GetValue(model)!).Execute(null);
                    for (var attempt = 0; attempt < 100 && FindTimeFormat("24h") is null; attempt++) await Task.Delay(50);
                    Check(FindTimeFormat("24h") is not null, "The installed Registry restores the default through the versioned write API.");
                    Console.WriteLine("The installed Registry read the saved time format and restored its default over loopback HTTP.");
                }
                if (performanceEndpoint is not null)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    var sample = model.GetType().GetProperty("Snapshot")!;
                    for (var attempt = 0; attempt < 200 && sample.GetValue(model) is null; attempt++)
                        await Task.Delay(50);
                    Check(sample.GetValue(model) is RelaxKonOS.Protocol.SystemMonitor.PerformanceRealtimeSnapshotDto { Memory.TotalBytes: > 0 },
                        "The installed Task Manager displays native host memory data through the SDK.");
                    var switchToProcesses = (System.Windows.Input.ICommand)model.GetType().GetProperty("SwitchToProcessesCommand")!.GetValue(model)!;
                    model.GetType().GetProperty("ProcessFilter")!.SetValue(model, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    switchToProcesses.Execute(null);
                    var rows = (System.Collections.IEnumerable)model.GetType().GetProperty("FilteredProcesses")!.GetValue(model)!;
                    bool HasDesktopProcess() => rows.Cast<object>().Any(row => row.GetType().GetProperty("Process")!.GetValue(row)
                        is RelaxKonOS.Protocol.SystemMonitor.ProcessInfoDto process && process.Id == Environment.ProcessId);
                    for (var attempt = 0; attempt < 200 && !HasDesktopProcess(); attempt++)
                        await Task.Delay(50);
                    Check(HasDesktopProcess(), "The installed Task Manager lists the running desktop test process through the ordinary process query.");
                    Console.WriteLine("The installed Task Manager received native performance and process data over loopback HTTP.");
                }
                var workspaceType = applicationId switch
                {
                    "relaxkonos.registry" => "RelaxKonOS.App.Registry.RegistryView",
                    "relaxkonos.taskmanager" => "RelaxKonOS.App.TaskManager.Views.TaskManagerMainView",
                    "relaxkonos.browser" => typeof(TextBlock).FullName,
                    "relaxkonos.terminal" => "RelaxKonOS.App.Terminal.TerminalWorkspaceView",
                    "relaxkonos.ssh-files" => "RelaxKonOS.UI.SshFiles.SshFileBrowserView",
                    "relaxkonos.certificates" => "RelaxKonOS.App.Certificates.Views.CertificateManagerWorkspace",
                    "relaxkonos.webservers" => "RelaxKonOS.App.WebServers.Views.WebServerManagerWorkspace",
                    "relaxkonos.git" => "RelaxKonOS.App.Git.Views.GitClientWorkspace",
                    "relaxkonos.application-deployments" => "RelaxKonOS.App.ApplicationDeployments.Views.ApplicationDeploymentsWorkspace",
                    "relaxkonos.port-forwarding" => "RelaxKonOS.App.PortForwarding.Views.PortForwardingMainView",
                    "relaxkonos.processguardian" => typeof(DockPanel).FullName,
                    "relaxkonos.tunnels" => "RelaxKonOS.App.Tunnels.Views.TunnelManagerView",
                    "relaxkonos.database" => "RelaxKonOS.App.Databases.Views.DatabaseWorkspace",
                    "relaxkonos.tailscale" => "RelaxKonOS.App.Tailscale.Views.TailscaleWorkspace",
                    "relaxkonos.squid" => "RelaxKonOS.App.Squid.Views.SquidWorkspace",
                    "relaxkonos.proxy-server" => "RelaxKonOS.App.ProxyServer.Views.ProxyServerWorkspace",
                    "relaxkonos.proxy" => "RelaxKonOS.App.ProxyManager.Views.ProxyManagerWorkspace",
                    "relaxkonos.firewall" => "RelaxKonOS.App.Firewall.Views.FirewallWorkspace",
                    "relaxkonos.file-services" => "RelaxKonOS.App.FileServices.Views.FileServicesWorkspace",
                    _ => "RelaxKonOS.App.Docker.Views.DockerManagerWorkspace",
                };
                Check(workspace.Info.OwnerAppId == id && workspace.View.Content?.GetType().FullName == workspaceType,
                    "The installed entry point must create its own workspace.");
                if (isSshFiles)
                {
                    Check(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(workspace.View.Content!.GetType().Assembly)?.IsCollectible == true,
                        "SSH Files must display the view loaded from the installed package.");
                    Check(await packages.LaunchAsync(applicationId), "The SSH Files package must support multiple windows.");
                    var second = windows.Windows.Single(window => !ReferenceEquals(window, workspace));
                    var sessionField = workspace.View.Content!.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    Check(ReferenceEquals(sessionField.GetValue(workspace.View.Content), sessionField.GetValue(second.View.Content)),
                        "SSH Files windows from the same package and connection must share clipboard and drag state.");
                    windows.Close(second);
                    if (sftpPort is not null)
                    {
                        var view = workspace.View.Content!;
                        var viewType = view.GetType();
                        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
                        await (Task)viewType.GetMethod("NavigateAsync", members)!.Invoke(view, ["/", true, false])!;
                        var entries = (Array)viewType.GetField("_entries", members)!.GetValue(view)!;
                        Check(entries.Cast<object>().Any(entry =>
                            Equals(entry.GetType().GetProperty("Name")!.GetValue(entry), "fixture.txt")),
                            $"The official SSH Files window must list the loopback fixture. Status: {((Control)view).FindControl<TextBlock>("StatusText")?.Text}");
                        var files = (ISshFileSession)sessionField.GetValue(view)!;
                        var connection = files.Connection!;
                        using var downloaded = new MemoryStream();
                        await files.DownloadFileAsync(connection, "/fixture.txt", downloaded, _ => { });
                        Check(downloaded.ToArray().AsSpan().SequenceEqual("RelaxKonOS SFTP fixture\n"u8),
                            "The package file capability must download the fixture's exact bytes.");
                        var uploadedBytes = "RelaxKonOS upload fixture\n"u8.ToArray();
                        using var upload = new MemoryStream(uploadedBytes);
                        Check(await files.UploadFileAsync(connection, upload, "/upload.txt", _ => { }),
                            "The package file capability must upload a new file.");
                        using var overwrite = new MemoryStream("replacement"u8.ToArray());
                        Check(!await files.UploadFileAsync(connection, overwrite, "/upload.txt", _ => { }),
                            "The package file capability must refuse to replace an existing file.");
                        using var roundTrip = new MemoryStream();
                        await files.DownloadFileAsync(connection, "/upload.txt", roundTrip, _ => { });
                        Check(roundTrip.ToArray().AsSpan().SequenceEqual(uploadedBytes),
                            "An upload must preserve the original bytes after a rejected overwrite.");
                        Check(!await files.RenamePathAsync(connection, "/upload.txt", "/fixture.txt"),
                            "Rename must refuse an existing destination.");
                        Check(await files.RenamePathAsync(connection, "/upload.txt", "/renamed.txt"),
                            "The uploaded file must be renamed.");
                        var renamed = await files.ReadDirectoryAsync(connection, "/");
                        Check(renamed.Items.Any(item => item.Name == "renamed.txt")
                            && renamed.Items.All(item => item.Name != "upload.txt"),
                            "Rename must replace the old directory entry.");
                        using var renamedFile = new MemoryStream();
                        await files.DownloadFileAsync(connection, "/renamed.txt", renamedFile, _ => { });
                        Check(renamedFile.ToArray().AsSpan().SequenceEqual(uploadedBytes),
                            "Rename must preserve the file contents.");
                        await files.DeletePathsAsync(connection, [("/renamed.txt", false)]);
                        var remaining = await files.ReadDirectoryAsync(connection, "/");
                        Check(remaining.Items is [{ Name: "fixture.txt" }],
                            "Delete must remove only the selected file.");

                        var uploadTree = Directory.CreateDirectory(Path.Combine(root, "upload-tree"));
                        var uploadNested = uploadTree.CreateSubdirectory("nested");
                        uploadTree.CreateSubdirectory("empty");
                        await File.WriteAllBytesAsync(Path.Combine(uploadTree.FullName, "root.txt"), uploadedBytes);
                        await File.WriteAllBytesAsync(Path.Combine(uploadNested.FullName, "child.txt"), "nested fixture"u8.ToArray());
                        Check(await files.UploadDirectoryAsync(connection, uploadTree, "/tree", (_, _, _) => { }, CancellationToken.None),
                            "The package file capability must upload a nested directory.");
                        Check(!await files.UploadDirectoryAsync(connection, uploadTree, "/tree", (_, _, _) => { }, CancellationToken.None),
                            "Directory upload must refuse to merge into an existing remote directory.");
                        var downloadRoot = Directory.CreateDirectory(Path.Combine(root, "download-tree")).FullName;
                        await files.DownloadPathsAsync(connection, downloadRoot, [("/tree", "tree", true)],
                            (_, _, _) => { }, CancellationToken.None);
                        var rootFile = await File.ReadAllBytesAsync(Path.Combine(downloadRoot, "tree", "root.txt"));
                        var childFile = await File.ReadAllBytesAsync(Path.Combine(downloadRoot, "tree", "nested", "child.txt"));
                        Check(rootFile.AsSpan().SequenceEqual(uploadedBytes) && childFile.AsSpan().SequenceEqual("nested fixture"u8)
                            && Directory.Exists(Path.Combine(downloadRoot, "tree", "empty")),
                            "Directory round-trip must preserve nested contents and empty directories.");
                        var moves = new List<(int Index, bool Moved)>();
                        await files.TransferPathsAsync(connection, "/tree", [("/fixture.txt", "fixture.txt", false, false)],
                            copy: true, (index, moved) => moves.Add((index, moved)));
                        var sourceAfterCopy = await files.ReadDirectoryAsync(connection, "/");
                        var destinationAfterCopy = await files.ReadDirectoryAsync(connection, "/tree");
                        Check(moves.Count == 0 && sourceAfterCopy.Items.Any(item => item.Name == "fixture.txt")
                            && destinationAfterCopy.Items.Any(item => item.Name == "fixture.txt"),
                            "Remote copy must retain the source and create a destination without reporting a move.");
                        await files.TransferPathsAsync(connection, "/tree/empty", [("/tree/fixture.txt", "fixture.txt", false, false)],
                            copy: false, (index, moved) => moves.Add((index, moved)));
                        var sourceAfterMove = await files.ReadDirectoryAsync(connection, "/tree");
                        using var movedFile = new MemoryStream();
                        await files.DownloadFileAsync(connection, "/tree/empty/fixture.txt", movedFile, _ => { });
                        Check(moves is [(0, true)] && sourceAfterMove.Items.All(item => item.Name != "fixture.txt")
                            && movedFile.ToArray().AsSpan().SequenceEqual("RelaxKonOS SFTP fixture\n"u8),
                            "Remote move must report success, remove the old entry and preserve the copied bytes.");

                        var pin = typeof(SshDesktopSession).GetProperty(nameof(SshDesktopSession.HostKeyFingerprint))!;
                        var expected = pin.GetValue(sshFixture);
                        pin.SetValue(sshFixture, ServerHostTrustRules.Fingerprint("different key"u8));
                        try
                        {
                            using var rejected = new MemoryStream();
                            try
                            {
                                await files.DownloadFileAsync(connection, "/fixture.txt", rejected, _ => { });
                                throw new InvalidOperationException("A mismatched SSH host key was accepted.");
                            }
                            catch (Renci.SshNet.Common.SshConnectionException)
                            {
                                Check(rejected.Length == 0, "A rejected host must not deliver any bytes.");
                            }
                        }
                        finally { pin.SetValue(sshFixture, expected); }
                        Console.WriteLine("SSH Files verified file operations, remote copy and move, nested directory round-trip, overwrite protection and host-key rejection over loopback SFTP.");
                    }
                }
                if (isDatabase)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    Check(model.GetType().GetProperty("CanManage")?.GetValue(model) is false
                        && model.GetType().GetProperty("Snapshot")?.GetValue(model) is RelaxKonOS.Protocol.Databases.PostgreSqlState { Installed: true },
                        "The installed database workspace reads PostgreSQL and honors a read-only grant.");
                }
                if (isApplicationDeployments)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    Check(model.GetType().FullName == "RelaxKonOS.App.ApplicationDeployments.ApplicationDeploymentsViewModel"
                        && Equals(model.GetType().GetProperty("CanManage")?.GetValue(model), submitDeployment),
                        "The installed deployment workspace honors its deployment grant.");
                    if (submitDeployment)
                        await SubmitInstalledDeploymentAsync(windows, model);
                    else if (deploymentsEndpoint is not null)
                    {
                        var rows = (System.Collections.IEnumerable)model.GetType().GetProperty("Applications")!.GetValue(model)!;
                        bool HasSavedDefinition() => rows.Cast<object>().Any(row => row.GetType().GetProperty("Name")!.GetValue(row) is "renamed");
                        for (var attempt = 0; attempt < 100 && !HasSavedDefinition(); attempt++) await Task.Delay(50);
                        Check(HasSavedDefinition(), "The installed deployment workspace lists the renamed definition from the production server API.");
                        Console.WriteLine("The installed Application Deployments workspace listed a saved definition over loopback HTTP.");
                    }
                }
                if (isProxyManager)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    Check(model.GetType().GetProperty("Overview")?.GetValue(model) is not null
                        && model.GetType().GetProperty("HasManagePermission")?.GetValue(model) is false
                        && model.GetType().GetProperty("HasTunManagePermission")?.GetValue(model) is false
                        && model.GetType().GetProperty("UpdateAllSubscriptionsCommand")?.GetValue(model) is System.Windows.Input.ICommand refreshAll
                        && !refreshAll.CanExecute(null),
                        "The installed Proxy Manager honors a read-only grant.");
                }
                if (isProxyServer)
                {
                    var model = ((Control)workspace.View.Content!).DataContext!;
                    Check(model.GetType().GetProperty("CanRead")?.GetValue(model) is true
                        && model.GetType().GetProperty("CanManage")?.GetValue(model) is false
                        && model.GetType().GetProperty("CanExport")?.GetValue(model) is false,
                        "The installed Proxy Server workspace honors a read-only grant.");
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
            var verifiedCandidate = await installer.InspectLocalAsync(path);
            DeveloperAppInfo upgraded;
            if (isSshFiles)
            {
                var deployment = new JointAppDeploymentService(null!, null!, installer, packages, applications, journal);
                var review = await deployment.PrepareAsync(verifiedCandidate with { DeleteWhenFinished = true }, [], CancellationToken.None);
                upgraded = await deployment.ExecuteAsync(review, CancellationToken.None);
                await using var lease = await journal.OpenAsync(review.Plan, CancellationToken.None);
                Check(lease.Checkpoint?.Verified == true && !File.Exists(path),
                    "The staged archive must be cleaned up after the verified deployment checkpoint.");
            }
            else
                upgraded = await InstallForLifecycleCheckAsync(installer, verifiedCandidate);
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
            var (releaseReader, releaseApplications, _, _) = CreateRuntime();
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

        async Task CheckOnlineRecoveryAsync(OfficialDesktopRelease release)
        {
            var onlineDirectory = Directory.CreateDirectory(Path.Combine(root, "OnlineOfficialApps")).FullName;
            var catalogUri = new Uri("https://packages.example.test/desktop/catalog.json");
            await File.WriteAllTextAsync(Path.Combine(onlineDirectory, OfficialDesktopPackages.SourceFile),
                System.Text.Json.JsonSerializer.Serialize(new { catalogUri }));
            var archiveUri = new Uri(catalogUri, OfficialDesktopCatalogRules.ArtifactPath(release.AppId, release.Version));
            var archive = await File.ReadAllBytesAsync(artifact);
            var broken = archive.ToArray();
            broken[0] ^= 1;
            var catalogDocument = new OfficialDesktopCatalogDocument(OfficialDesktopCatalogRules.SchemaVersion,
                [new(release.AppId, release.Version, release.DisplayName, release.Description, release.Sha256, release.Bytes)]);
            var json = System.Text.Json.JsonSerializer.Serialize(catalogDocument,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var downloads = 0;
            var corruptDownload = false;
            using var online = new HttpClient(new OfficialRecoveryTransport(request =>
            {
                if (request.RequestUri == catalogUri) return new StringContent(json);
                if (request.RequestUri != archiveUri) throw new InvalidOperationException("Unexpected official recovery request.");
                downloads++;
                return new ByteArrayContent(corruptDownload ? broken : archive);
            }));
            var (onlinePackages, onlineApplications, _, _) = CreateRuntime(onlineDirectory, online);
            var onlineInstaller = new AppPackageInstallerService(onlinePackages, null!, catalog);
            var onlineJournalPath = Path.Combine(root, "resume-online-catalog");
            var onlineJournal = new FileDeploymentJournal(onlineJournalPath);
            var deployment = new JointAppDeploymentService(null!, null!, onlineInstaller, onlinePackages, onlineApplications, onlineJournal);
            var initial = await onlineInstaller.BrowseOfficialAsync(CancellationToken.None);
            var staged = await onlineInstaller.StageOfficialAsync(initial.Releases.Single(), CancellationToken.None);
            try
            {
                var review = await deployment.PrepareAsync(staged, [], CancellationToken.None);
                await using var lease = await onlineJournal.OpenAsync(review.Plan, CancellationToken.None);
                await lease.SaveAsync(new DeploymentCheckpoint(0, false), CancellationToken.None);
            }
            finally { onlineInstaller.Discard(staged); }
            Check(!File.Exists(staged.LocalPath), "The original downloaded archive must be gone before recovery.");

            var (reopenedPackages, reopenedApplications, _, _) = CreateRuntime(onlineDirectory, online);
            var reopenedInstaller = new AppPackageInstallerService(reopenedPackages, null!, catalog);
            var reopened = new JointAppDeploymentService(null!, null!, reopenedInstaller, reopenedPackages, reopenedApplications,
                new FileDeploymentJournal(onlineJournalPath));
            var current = await reopenedInstaller.BrowseOfficialAsync(CancellationToken.None);
            corruptDownload = true;
            Check((await reopened.RecoverArchivesAsync(current.Releases, CancellationToken.None)).Count == 0 && downloads == 2,
                "A same-length online archive with incorrect bytes must be rejected.");
            corruptDownload = false;
            var recovered = await reopened.RecoverArchivesAsync(current.Releases, CancellationToken.None);
            try
            {
                Check(recovered is [{ IsRecoveredDeployment: true, DeleteWhenFinished: true }]
                    && recovered[0].LocalPath != staged.LocalPath && downloads == 3
                    && reopenedPackages.FindInstalled(applicationId) is null,
                    "Online recovery must stage a fresh verified archive without installing it.");
            }
            finally { foreach (var candidate in recovered) reopenedInstaller.Discard(candidate); }
        }

        (DeveloperPackageManager, ApplicationManager, WindowManager, SshDesktopSession) CreateRuntime(
            string? officialDirectory = null, HttpClient? officialHttp = null)
        {
            var windows = new WindowManager();
            var apps = new ApplicationManager(windows, services);
            var permissions = new RoappCatalogChecks.Permissions();
            var auth = isDatabase ? DispatchProxy.Create<IAuthSession, DatabasePackageAuthFixture>()
                : isTailscale ? DispatchProxy.Create<IAuthSession, TailscaleScopeAuthFixture>() : isProxyServer || isProxyManager
                ? DispatchProxy.Create<IAuthSession, ProxyServerScopeAuthFixture>()
                : DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
            if (isDatabase)
            {
                databaseAuth = (DatabasePackageAuthFixture)(object)auth;
                compatibility.DatabaseEvaluator = new ApplicationCompatibilityService(auth, windows, null!, new EmptyModuleCatalog(), null!);
            }
            if (isTerminal || isSshFiles)
                ((EventAlertsAuthFixture)(object)auth).State = AuthSessionState.Unauthenticated;
            if (isBrowser)
            {
                browserAuth = (EventAlertsAuthFixture)(object)auth;
                browserAuth.State = AuthSessionState.Unauthenticated;
            }
            var memory = new UsageMemoryStore(Path.Combine(root, "usage-memory.json"));
            var appPermissions = new CertificateCapabilityChecks.Permission(applicationId);
            if (alertEndpoint is not null)
            {
                ((EventAlertsAuthFixture)(object)auth).ServiceId = alertEndpoint.AbsoluteUri;
                appPermissions.Granted.Add(AppPermissions.ServerEventAlertsRead);
            }
            if (performanceEndpoint is not null)
            {
                ((EventAlertsAuthFixture)(object)auth).ServiceId = performanceEndpoint.AbsoluteUri;
                appPermissions.Granted.Add(AppPermissions.ServerMetricsRead);
                appPermissions.Granted.Add(AppPermissions.ServerProcessesManage);
            }
            if (registryEndpoint is not null)
            {
                var registryAuth = (EventAlertsAuthFixture)(object)auth;
                registryAuth.ServiceId = registryEndpoint.AbsoluteUri;
                registryAuth.UserId = Guid.Parse(Environment.GetEnvironmentVariable("RK_TEST_REGISTRY_USER")!);
                appPermissions.Granted.Add(AppPermissions.ServerRegistryRead);
                appPermissions.Granted.Add(AppPermissions.ServerRegistryWrite);
            }
            appPermissions.Granted.Add(AppPermissions.ServerCertificatesRead);
            appPermissions.Granted.Add(AppPermissions.ServerWebServersRead);
            var certificates = isCertificates || isDatabase ? DispatchProxy.Create<IServerCertificates, CertificateCapabilityClient>() : null;
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
            if (submitDeployment) appPermissions.Granted.Add(AppPermissions.ServerApplicationDeploymentsManage);
            IServerApplicationDeployments? deployments = null;
            if (deploymentsEndpoint is not null)
            {
                ((EventAlertsAuthFixture)(object)auth).ServiceId = deploymentsEndpoint.AbsoluteUri;
                deployments = new RelaxKonOS.Client.Apps.ApplicationDeployments.RemoteApplicationDeploymentClient(http, auth);
            }
            else if (isApplicationDeployments)
            {
                deployments = DispatchProxy.Create<IServerApplicationDeployments, DeploymentHostStub>();
                ((DeploymentHostStub)(object)deployments).Seed();
            }
            appPermissions.Granted.Add(AppPermissions.ServerTunnelsRead);
            var tunnels = isTunnels ? DispatchProxy.Create<IServerTunnels, TunnelCapabilityClient>() : null;
            appPermissions.Granted.Add(AppPermissions.ServerSquidRead);
            var squid = isSquid ? new SquidWorkspaceChecks.SquidClientStub() : null;
            appPermissions.Granted.Add(AppPermissions.ServerProxyServerRead);
            var proxyServer = isProxyServer ? DispatchProxy.Create<IServerProxyServer, ProxyServerScopeTransport>() : null;
            appPermissions.Granted.Add(AppPermissions.ServerProxyRead);
            var proxyManager = isProxyManager ? new ProxyWorkspaceChecks.ProxyRepositoryFixture() : null;
            appPermissions.Granted.Add(AppPermissions.ServerTailscaleRead);
            var tailscale = isTailscale ? DispatchProxy.Create<IServerTailscale, TailscaleScopeTransport>() : null;
            appPermissions.Granted.Add(AppPermissions.ServerDatabasesRead);
            var scopedPermissions = alertEndpoint is not null || performanceEndpoint is not null || registryEndpoint is not null || isDatabase || isCertificates || isWebServers || isGit || isFileServices || isFirewall
                || isGuardian || isPortForwarding || isApplicationDeployments || isTunnels || isSquid || isProxyServer || isProxyManager || isTailscale;
            var postgres = isDatabase ? new OfficialPostgreSqlFixture() : null;
            var ssh = new SshDesktopSession(null!, settings);
            if (isSshFiles)
            {
                typeof(SshDesktopSession).GetProperty(nameof(SshDesktopSession.Endpoint))!
                    .SetValue(ssh, sftpPort is null
                        ? ServerCenterSshEndpoint.Create("example.test", 22, "preview")
                        : ServerCenterSshEndpoint.Create("127.0.0.1", int.Parse(sftpPort), "preview"));
                if (sftpPort is not null)
                {
                    typeof(SshDesktopSession).GetProperty(nameof(SshDesktopSession.Password))!
                        .SetValue(ssh, Environment.GetEnvironmentVariable("RK_TEST_SFTP_PASSWORD"));
                    typeof(SshDesktopSession).GetProperty(nameof(SshDesktopSession.HostKeyFingerprint))!
                        .SetValue(ssh, Environment.GetEnvironmentVariable("RK_TEST_SFTP_FINGERPRINT"));
                }
            }
            var context = new ExternalAppContextFactory(scopedPermissions ? appPermissions : permissions,
                scopedPermissions ? appPermissions : null!, language, settings, null!, auth, null!,
                windows, performanceEndpoint is null ? null! : new RelaxKonOS.Client.Apps.TaskManager.TaskManagerClient(http, auth),
                new ExplorerClient(http, auth, memory), ssh,
                new SshExplorerClient(ssh), DispatchProxy.Create<ISettingsNavigation, UnusedPreviewServices>(),
                null!, null!, apps, memory, null!, alertEndpoint is null ? null! : new RemoteEventAlertClient(http, auth), null!, null!,
                registry: registryEndpoint is null ? null : new RelaxKonOS.Client.Apps.Registry.RegistryClient(http, auth, null!, new PresentationSettingsChanges()),
                browserData: isBrowser ? browserData : null, certificates: certificates, webServers: webServers, git: git,
                fileServices: fileServices, firewall: firewall, guardian: guardian, portForwarding: forwarding, applicationDeployments: deployments, tunnels: tunnels, createSquidClient: squid is null ? null : () => squid,
                createProxyServerClient: proxyServer is null ? null : () => proxyServer,
                createProxyClient: proxyManager is null ? null : () => proxyManager, tailscale: tailscale,
                createPostgreSqlClient: postgres is null ? null : () => postgres);
            return (new DeveloperPackageManager(apps, context, windows, permissions, new RoappCatalogChecks.Diagnostics(),
                drive, officialDirectory ?? source, officialHttp), apps, windows, ssh);
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
            ? installer.InstallAsync(candidate, default, (_, _) => Task.FromResult(new AppDeploymentReceipt(Guid.NewGuid(), null)))
            : installer.InstallAsync(candidate);

        Task WriteProfileAsync(string policy, string? minimum = null)
        {
            var entry = new[] { new { appId = applicationId, minimumVersion = minimum ?? (isGit ? "0.3.1" : isDocker ? "0.3.0" : "1.0.0"), installPolicy = policy } };
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
        public IApplicationCompatibilityEvaluator? DatabaseEvaluator { get; set; }
        public ApplicationCompatibilityResult Evaluate(ApplicationManifest manifest)
        {
            if (manifest.Id.Value == "relaxkonos.taskmanager" && manifest.Server.RequiredCapabilities.Count > 0 && !TaskManagerServerAvailable)
                return new(ApplicationCompatibilityStatus.ServerUnavailable);
            if (manifest.Id.Value == "relaxkonos.database" && DatabaseEvaluator is { } evaluator)
                return evaluator.Evaluate(manifest);
            return ApplicationCompatibilityResult.Compatible;
        }
    }

    private sealed class EmptyModuleCatalog : RelaxKonOS.Client.Services.Modules.IServerModuleCatalog
    {
        public IReadOnlyList<ServerModuleDto>? Cached => null;
        public string? LastFailure => null;
        public Task<IReadOnlyList<ServerModuleDto>?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ServerModuleDto>?>(null);
        public void EnsureLoaded() { }
        public void Invalidate() { }
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

internal sealed class OfficialPostgreSqlFixture : RelaxKonOS.AppSDK.IServerPostgreSql
{
    public Task<RelaxKonOS.Protocol.Databases.PostgreSqlState> ReadAsync(CancellationToken cancellationToken, Guid? instance = null) =>
        Task.FromResult<RelaxKonOS.Protocol.Databases.PostgreSqlState>(new(true, true, true,
            "16.15-0ubuntu0.24.04.1", 2, null, 55432, false, false, null, null, null));

    public Task<RelaxKonOS.Protocol.Databases.PostgreSqlState> MaintenancePlanAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<RelaxKonOS.Protocol.Databases.PostgreSqlState> MigrationPlanAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task StageCredentialAsync(RelaxKonOS.Protocol.Databases.PostgreSqlCredentialRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

public class DatabasePackageAuthFixture : TailscaleScopeAuthFixture
{
    public bool DatabaseAvailable { get; set; } = true;

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_CurrentServer"
        ? new ServerDescriptorDto(HostPlatformKind.Linux, DatabaseAvailable ? [ServerCapabilities.Database] : [])
        : base.Invoke(method, args);
}
