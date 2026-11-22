using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.AspNetCore.Authentication;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Startup;

internal static class JointDeploymentAcceptance
{
    private const string ModuleId = "fixture-module";
    private const string AppIdText = "relaxkonos.welcome";

    public static void Run(string archive, IReadOnlyList<string> bundledModuleArchives) =>
        RunWithDispatcher(() => RunAsync(Path.GetFullPath(archive), bundledModuleArchives.Select(Path.GetFullPath).ToArray()));

    public static void RunSignedGit(string archive) => RunWithDispatcher(() => RunSignedGitAsync(Path.GetFullPath(archive)));

    private static async Task RunSignedGitAsync(string archive)
    {
        var parent = Path.Combine(AppContext.BaseDirectory, "test-data");
        var root = Directory.CreateDirectory(Path.Combine(parent, "signed-git-joint-" + Guid.NewGuid().ToString("N"))).FullName;
        TestHost? host = null;
        try
        {
            var official = Directory.CreateDirectory(Path.Combine(root, "OfficialApps")).FullName;
            var appArchive = Path.Combine(official, "relaxkonos.git.roapp");
            const string version = "99.0.0";
            await RewriteDesktopArchiveAsync(archive, appArchive, manifest =>
                manifest["dependencies"]!["required"]![0]!["minimumVersion"] = version);
            var serverRoot = Directory.CreateDirectory(Path.Combine(root, "server")).FullName;
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var key = Path.Combine(root, "signing.pem");
            await File.WriteAllTextAsync(key, signer.ExportPkcs8PrivateKeyPem());
            var now = DateTimeOffset.UtcNow;
            var rid = ModulePackagePlatform.CurrentRid;
            var package = await ModulePackageBuilder.BuildAsync(new(Path.GetDirectoryName(CollectibleGitExecution.ResolveBundledPath())!,
                Path.Combine(root, "module"), ServerModules.GitId, version, "joint-git-" + rid, "fixture", "official", rid,
                CollectibleGitExecution.AssemblyFile, CollectibleGitExecution.EntryType, "joint-git", new string('a', 40), "official", 1,
                "https://modules.example/", key, "fixture-key", 3600, Capabilities: [ServerCapabilities.Git],
                CapabilityContractVersions: new Dictionary<string, string> { [ServerCapabilities.Git] = GitApiRoutes.CapabilityContractVersion }), now);
            var policy = new ModulePackageTrustPolicy(1, "official", 1, true, "fixture", "official", "https://modules.example/",
                [ServerModules.GitId], new Dictionary<string, string> { ["fixture-key"] = package.PublicKeySpkiBase64 }, 3600);
            var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                ModuleManagement = new { Authority = "managed" },
                ModulePackages = new { Sources = new Dictionary<string, ModulePackageTrustPolicy> { ["official"] = policy } },
            }, RelaxKonOSJsonOptions.Default))).Build();
            string serviceId;
            using (var manager = new ModuleManagementStore(config, serverRoot)) serviceId = manager.ServiceId;
            using (var catalogs = new ModulePackageCatalogStore(serverRoot, serviceId))
                catalogs.Accept(await File.ReadAllBytesAsync(package.CatalogPath), policy,
                    new(serviceId, rid, ModulePackageHostConfiguration.HostApiVersion, false), ModulePackageCatalogStore.InitialRevision,
                    ModuleTestAuthentication.Admin, "joint-git", now);

            host = await TestHost.StartAsync(serverRoot, config);
            var auth = DispatchProxy.Create<IAuthSession, SessionProxy>();
            var session = (SessionProxy)(object)auth;
            session.Url = host.Url;
            using var factory = new FixtureHttpFactory(await File.ReadAllBytesAsync(package.ArchivePath));
            using var services = new ServiceCollection().BuildServiceProvider();
            var windows = new RelaxKonOS.WindowManager.WindowManager();
            var apps = new ApplicationManager(windows, services);
            var packages = new DeveloperPackageManager(apps, null!, windows, DispatchProxy.Create<IAppPermissionManager, PermissionProxy>(),
                new Diagnostics(), new VirtualSystemDrive(Path.Combine(root, "desktop")), official, moduleHttp: factory, moduleSession: auth);
            await packages.LoadInstalledAsync();
            using var trust = new ComponentCatalogSourceStore(Path.Combine(root, "sources"), "joint-git");
            var installer = new AppPackageInstallerService(packages, null!, trust);
            var joint = new JointAppDeploymentService(factory, auth, installer, packages, apps, new(Path.Combine(root, "journal")));
            var candidate = await installer.InspectLocalAsync(appArchive);
            var review = await joint.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps.Length == 1, "the Git desktop package selects the independent signed server release");
            await ExpectRestartAsync(joint, review);
            Require(!apps.IsRegistered(new AppId(candidate.Manifest.Id)), "the desktop waits for the signed Git package to become effective");
            await host.DisposeAsync();
            host = null;
            host = await TestHost.StartAsync(serverRoot, config);
            session.Url = host.Url;
            var resumed = await joint.FindPendingAsync(candidate, default) ?? throw new InvalidOperationException("Git joint plan was not recoverable.");
            var installed = await joint.ExecuteAsync(resumed, default);
            Require(apps.IsRegistered(new AppId(installed.Id)) && InstalledModuleStore.Read(serverRoot).Single() is { Version: version }
                && ModulePackageOperationStore.Read(serverRoot).Count == 1,
                "after restart the desktop registers Git against the signed execution package without resubmitting installation");
            Require(await packages.UninstallAsync(installed.Id) && !apps.IsRegistered(new AppId(installed.Id))
                && InstalledModuleStore.Read(serverRoot).Single().ArtifactSha256 == package.ArchiveSha256,
                "uninstalling the desktop Git package releases the app without removing the server module");
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            if (Path.GetDirectoryName(root) == parent)
            {
                // Windows can retain loaded application assemblies until this process exits.
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Console.WriteLine($"Fixture cleanup deferred until process exit: {root}"); }
            }
        }
    }

    public static void RunLiveDatabases(string server, string archive, string state, string phase) =>
        RunWithDispatcher(() => RunLiveAsync(new Uri(server), Path.GetFullPath(archive), Path.GetFullPath(state), phase, ServerModules.DatabaseId));

    public static void RunLiveGit(string server, string archive, string state, string repository) =>
        RunWithDispatcher(() => RunLiveAsync(new Uri(server), Path.GetFullPath(archive), Path.GetFullPath(state), "reuse",
            ServerModules.GitId, repository));

    private static void RunWithDispatcher(Func<Task> run)
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var task = run();
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new TimeoutException("Joint deployment acceptance did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static async Task RunLiveAsync(Uri server, string archive, string state, string phase, string moduleId,
        string? repository = null)
    {
        if (server.Scheme != "http" || server.Host != "127.0.0.1" || server.PathAndQuery != "/"
            || server.UserInfo.Length != 0 || server.Fragment.Length != 0 || phase is not ("start" or "resume" or "reuse" or "uninstall"))
            throw new ArgumentException("Expected a loopback Server and a start, resume, reuse or uninstall phase.");
        var username = Environment.GetEnvironmentVariable("RK_JOINT_FIXTURE_USER");
        var password = Environment.GetEnvironmentVariable("RK_JOINT_FIXTURE_PASSWORD");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Provide the dedicated fixture login.");
        using var loginHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var auth = new AuthSession(new RelaxKonOSClient(loginHttp), null!, null!);
        var platform = OperatingSystem.IsWindows() ? ClientPlatformKind.Windows : ClientPlatformKind.Linux;
        await auth.LoginAsync(ServerConnectionIdentityRules.Direct(server.AbsoluteUri),
            new(username, password, platform, "databases-joint-acceptance", "0.3.0"), false, false);
        using var moduleHttp = new HttpClient(new AuthenticatedHttpHandler(auth)
        {
            InnerHandler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false },
        });
        using var services = new ServiceCollection().BuildServiceProvider();
        using var trust = new ComponentCatalogSourceStore(Path.Combine(state, "trust"), "live-joint-fixture");
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        var applications = new ApplicationManager(windows, services);
        var drive = new VirtualSystemDrive(Path.Combine(state, "drive"));
        var permissions = DispatchProxy.Create<IAppPermissionManager, PermissionProxy>();
        var factory = new LiveHttpFactory(moduleHttp);
        var packages = new DeveloperPackageManager(applications, null!, windows, permissions, new Diagnostics(), drive,
            Path.GetDirectoryName(archive)!, moduleHttp: factory, moduleSession: auth);
        await packages.LoadInstalledAsync();
        if (phase == "uninstall")
        {
            Require(await packages.UninstallAsync(ServerModules.DatabaseId) && !applications.IsRegistered(new AppId(ServerModules.DatabaseId)),
                "the desktop uninstalls its own Databases application");
            return;
        }
        var installer = new AppPackageInstallerService(packages, null!, trust);
        var deployment = new JointAppDeploymentService(factory, auth, installer, packages,
            applications, new FileDeploymentJournal(Path.Combine(state, "journal")));
        var candidate = await installer.InspectLocalAsync(archive);
        Require(candidate.Manifest.Id == moduleId, $"the fixture uses the {moduleId} desktop package");
        if (moduleId == ServerModules.GitId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repository);
            var review = await deployment.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps.IsEmpty && review.Plan.OrderedReleases.Any(release =>
                    release.Package.Kind == DeploymentPackageKind.ServerModule && release.Package.Id == moduleId
                    && release.Satisfied && release.Capabilities.GetValueOrDefault(ServerCapabilities.Git) == RelaxKonOS.Protocol.Git.GitApiRoutes.CapabilityContractVersion),
                "Project Editor reuses the running Git module with its declared contract version");
            var installedGit = await deployment.ExecuteAsync(review, default);
            Require(installedGit.Id == moduleId && applications.IsRegistered(new AppId(moduleId)),
                "the desktop registers Project Editor after checking the live Git module");
            var git = new RelaxKonOS.Client.Apps.Git.RemoteGitClient(loginHttp, auth);
            var registered = await git.RegisterRepositoryAsync(new("Joint deployment fixture", repository));
            const string remoteName = "fixture-acceptance";
            Require((await git.ListRemotesAsync(registered.Id)).SingleOrDefault(remote => remote.Name == remoteName)
                    is { } remote && (remote.PushUrl ?? remote.FetchUrl) == repository + ".git",
                "the fixture push target is the dedicated local bare repository");
            var before = await git.GetGraphAsync(registered.Id);
            var beforeStatus = await git.GetStatusAsync(registered.Id);
            Require(before is [{ IsHead: true }] && beforeStatus.Staged.Select(item => item.Path).SequenceEqual(["staged.txt"])
                && beforeStatus.Untracked.Select(item => item.Path).SequenceEqual(["draft.txt"]),
                "the fixture starts with one Git commit and the isolated staged and untracked files");
            var commit = await git.CommitAsync(registered.Id, new("Project Editor fixture commit", ["draft.txt"]));
            Require(commit is { Success: true, Operation: "commit" }, "the desktop Git client commits the selected file");
            var after = await git.GetGraphAsync(registered.Id);
            var status = await git.GetStatusAsync(registered.Id);
            Require(after is [var head, var parent] && head.IsHead && parent.Sha == before[0].Sha
                && head.Parents.SequenceEqual([parent.Sha])
                && status.Staged.Select(item => item.Path).SequenceEqual(["staged.txt"])
                && status.Untracked.All(item => item.Path != "draft.txt"),
                "the commit advances HEAD and leaves unrelated staged work intact");
            var push = await git.PushAsync(registered.Id, new(SaveCredentials: false, LocalBranch: "main",
                Remote: remoteName, RemoteBranch: "fixture-verified"));
            Require(push is { Success: true, Operation: "push", RequiresCredentials: false },
                "the desktop Git client pushes the selected branch to the isolated bare repository");
            return;
        }
        if (phase == "start")
        {
            var review = await deployment.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps is [{ Package.Id: ServerModules.DatabaseId }],
                "the desktop prepares a signed Databases server package before installation");
            await ExpectRestartAsync(deployment, review);
            Require(!applications.IsRegistered(new AppId(ServerModules.DatabaseId)),
                "the desktop waits for the server module before registering Databases");
            return;
        }
        var resumed = phase == "reuse" ? await deployment.PrepareAsync(candidate, [], default)
            : await deployment.FindPendingAsync(candidate, default)
            ?? throw new InvalidOperationException("The desktop could not recover its original Databases deployment.");
        if (phase == "reuse")
            Require(resumed.Plan.PreparedSteps.IsEmpty
                && resumed.Plan.OrderedReleases.Any(release => release.Package.Kind == DeploymentPackageKind.ServerModule
                    && release.Package.Id == ServerModules.DatabaseId && release.Satisfied
                    && release.ArtifactIdentity.Split('/').Length == 3),
                "the desktop reuses the running signed Databases module");
        var installed = await deployment.ExecuteAsync(resumed, default);
        var manifest = applications.GetManifest(new AppId(installed.Id));
        Require(installed.Id == ServerModules.DatabaseId && manifest is not null,
            "the desktop plan registers Databases after verifying the running module");
        using var readinessServices = new ServiceCollection().AddSingleton(packages).AddSingleton(applications)
            .AddSingleton<RelaxKonOS.WindowManager.IWindowManager>(windows)
            .AddSingleton<IHttpClientFactory>(new LiveHttpFactory(moduleHttp)).BuildServiceProvider();
        using var readiness = new ApplicationAvailabilityService(auth, readinessServices, null!);
        Require(await readiness.RefreshAsync(manifest!) is { State: ComponentAvailabilityKind.NoPermission, PackageKind: null },
            "launch readiness accepts the signed module and reaches the separate component permission check");
    }

    private sealed class LiveHttpFactory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private static async Task RunAsync(string archive, IReadOnlyList<string> bundledModuleArchives)
    {
        var parent = Path.Combine(AppContext.BaseDirectory, "test-data");
        var root = Directory.CreateDirectory(Path.Combine(parent, "joint-deployment-" + Guid.NewGuid().ToString("N"))).FullName;
        var publish = Directory.CreateDirectory(Path.Combine(root, "module-publish")).FullName;
        var official = Directory.CreateDirectory(Path.Combine(root, "OfficialApps")).FullName;
        var serverRoot = Directory.CreateDirectory(Path.Combine(root, "server")).FullName;
        var packagePath = Path.Combine(official, AppIdText + ".roapp");
        TestHost? host = null;
        try
        {
            await PrepareDesktopArchiveAsync(archive, packagePath, optionalModuleId: ServerModules.FirewallId,
                capability: ServerCapabilities.Metrics, contractVersion: "2");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "FixtureModule.dll"), Path.Combine(publish, "FixtureModule.dll"));
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var key = Path.Combine(root, "fixture-signing-key.pem");
            await File.WriteAllTextAsync(key, signer.ExportPkcs8PrivateKeyPem());
            var now = DateTimeOffset.UtcNow;
            var rid = ModulePackagePlatform.CurrentRid;
            var build = await ModulePackageBuilder.BuildAsync(new(publish, Path.Combine(root, "module-package"), ModuleId,
                "1.0.0", "joint-fixture-1.0.0-" + rid, "fixture", "official", rid, "FixtureModule.dll",
                "FixtureModule.FixtureInstalledModule", "joint-fixture", new string('a', 40), "official", 1,
                "https://modules.example/", key, "fixture-key", 3600,
                Ui: new("Joint Fixture", "", "", null, "UNLICENSED"), Capabilities: [ServerCapabilities.Metrics],
                CapabilityContractVersions: new Dictionary<string, string> { [ServerCapabilities.Metrics] = "2" }), now);
            try
            {
                ModulePackageVerifier.ValidateManifest(build.Manifest with
                {
                    CapabilityContractVersions = new Dictionary<string, string> { [ServerCapabilities.Files] = "2" }
                });
                throw new InvalidOperationException("Expected an undeclared capability contract to be rejected.");
            }
            catch (ModulePackageException error) when (error.ProblemCode == "module-package-capability-contract-version-invalid") { }
            File.Delete(key);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModuleManagement:Authority"] = "managed",
                ["ModulePackages:Sources:official:SchemaVersion"] = "1",
                ["ModulePackages:Sources:official:SourceId"] = "official",
                ["ModulePackages:Sources:official:Revision"] = "1",
                ["ModulePackages:Sources:official:Enabled"] = "true",
                ["ModulePackages:Sources:official:PublisherId"] = "fixture",
                ["ModulePackages:Sources:official:Purpose"] = "official",
                ["ModulePackages:Sources:official:ArtifactOrigin"] = "https://modules.example/",
                ["ModulePackages:Sources:official:AuthorizedModuleIds:0"] = ModuleId,
                ["ModulePackages:Sources:official:PublicKeysSpki:fixture-key"] = build.PublicKeySpkiBase64,
                ["ModulePackages:Sources:official:MaximumValiditySeconds"] = "3600",
            }).Build();

            string serviceId;
            using (var store = new ModuleManagementStore(config, serverRoot)) serviceId = store.ServiceId;
            using (var catalog = new ModulePackageCatalogStore(serverRoot, serviceId))
                catalog.Accept(await File.ReadAllBytesAsync(build.CatalogPath), ModulePackageHostConfiguration.Source(config, "official"),
                    new ModulePackageContext(serviceId, rid, ModulePackageHostConfiguration.HostApiVersion, false),
                    ModulePackageCatalogStore.InitialRevision,
                    ModuleTestAuthentication.Admin, "joint-accept", now);

            host = await TestHost.StartAsync(serverRoot, config);
            var auth = DispatchProxy.Create<IAuthSession, SessionProxy>();
            var session = (SessionProxy)(object)auth;
            session.Url = host.Url;
            using var factory = new FixtureHttpFactory(await File.ReadAllBytesAsync(build.ArchivePath));
            using var services = new ServiceCollection().BuildServiceProvider();
            var windows = new RelaxKonOS.WindowManager.WindowManager();
            var applications = new ApplicationManager(windows, services);
            var drive = new VirtualSystemDrive(Path.Combine(root, "desktop-drive"));
            var permissions = DispatchProxy.Create<IAppPermissionManager, PermissionProxy>();
            var packages = new DeveloperPackageManager(applications, null!, windows, permissions, new Diagnostics(), drive, official,
                moduleHttp: factory, moduleSession: auth);
            await packages.LoadInstalledAsync();
            using var trust = new ComponentCatalogSourceStore(Path.Combine(root, "component-trust"), "joint-fixture");
            var installer = new AppPackageInstallerService(packages, null!, trust);
            var journalPath = Path.Combine(root, "journal");
            JointAppDeploymentService Service() => new(factory, auth, installer, packages, applications, new(journalPath));
            var candidate = await installer.InspectLocalAsync(packagePath);
            var joint = Service();
            const string prerequisiteId = "fixture.prerequisite";
            var prerequisitePath = Path.Combine(root, "prerequisite.roapp");
            await RewriteDesktopArchiveAsync(archive, prerequisitePath, manifest => manifest["id"] = prerequisiteId);
            await installer.InstallAsync(await installer.InspectLocalAsync(prerequisitePath));
            var mixedPath = Path.Combine(root, "mixed.roapp");
            await RewriteDesktopArchiveAsync(packagePath, mixedPath, manifest =>
            {
                manifest["id"] = "fixture.mixed";
                manifest["dependencies"]!["required"]!.AsArray().Add(new JsonObject
                {
                    ["package"] = new JsonObject { ["kind"] = (int)DeploymentPackageKind.Application, ["id"] = prerequisiteId },
                });
            });
            var mixedCandidate = await installer.InspectLocalAsync(mixedPath);
            var mixed = await joint.PrepareAsync(mixedCandidate, [], default);
            Require(mixed.Plan.OrderedReleases.Select(release => release.Package.Id)
                    .SequenceEqual([ModuleId, prerequisiteId, mixedCandidate.Manifest.Id]),
                "the mixed plan orders the module before the local prerequisite");
            factory.BeforeNextRequest = () => packages.UninstallAsync(prerequisiteId);
            try
            {
                await joint.ExecuteAsync(mixed, default);
                throw new InvalidOperationException("the mixed plan accepted a removed prerequisite");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-client-dependency-changed") { }
            Require(!applications.IsRegistered(new AppId(prerequisiteId))
                && ModulePackageOperationStore.Read(serverRoot).Count == 0,
                "removing a local prerequisite during snapshot observation prevents the module operation");
            var reopenedMixedJournal = new FileDeploymentJournal(journalPath);
            Require(await Service().FindPendingAsync(mixedCandidate, default) is null
                && (await reopenedMixedJournal.ReadPendingRecoveriesAsync(auth.CurrentUser!.Id, auth.ServiceId!))
                    .Any(recovery => recovery.Plan.Id == mixed.Plan.Id && recovery.RequiresReview),
                "the reopened journal offers a new review instead of resuming the invalidated mixed plan");

            var optional = candidate.Manifest.Dependencies!.Optional.Single();
            var requestsBeforeInvalidSelection = factory.Requests;
            try
            {
                await joint.PrepareAsync(candidate, [optional with { MinimumVersion = "9.0.0" }], default);
                throw new InvalidOperationException("an altered optional dependency was accepted");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-optional-selection-invalid") { }
            Require(factory.Requests == requestsBeforeInvalidSelection, "altered optional requirements are rejected before server requests");
            var withOptional = await joint.PrepareAsync(candidate, [optional], default);
            Require(withOptional.Plan.OrderedReleases.Any(release => release.Package == optional.Package && release.Satisfied),
                "selecting the declared optional module keeps it in the plan");
            var review = await joint.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps.Length == 1 && review.Plan.OrderedReleases[0].Package.Id == ModuleId
                && review.Plan.OrderedReleases.All(release => release.Package != optional.Package),
                "deselecting the optional module preserves the missing required signed module");
            var settings = new ShellSettings(null!);
            var localization = new LocalizationService(settings, new SshDesktopSession(null!, settings));
            using (var uiServices = new ServiceCollection().AddSingleton(localization).BuildServiceProvider())
            {
                typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, uiServices);
                using var model = new AppInstallerViewModel(installer) { JointDeployment = joint, CurrentPackage = candidate };
                model.SelectOptionalAsync = _ => Task.FromResult<IReadOnlyList<DeploymentRequirement>?>([]);
                var requestsBeforeLoginChange = 0;
                var confirmations = 0;
                model.ConfirmDeploymentAsync = _ =>
                {
                    if (++confirmations != 1) return Task.FromResult(false);
                    requestsBeforeLoginChange = factory.Requests;
                    factory.BeforeNextClient = () => session.Url = host.Url;
                    return Task.FromResult(true);
                };
                await model.InstallCommand.ExecuteAsync(null);
                Require(factory.Requests == requestsBeforeLoginChange
                    && !applications.IsRegistered(new AppId(AppIdText)),
                    "a replacement login during client binding sends no module request");
                await model.InstallCommand.ExecuteAsync(null);
                Require(confirmations == 2 && !applications.IsRegistered(new AppId(AppIdText)),
                    "retry after a replacement login requires a fresh confirmation");
            }
            factory.SubstituteServerIdentity = Guid.NewGuid().ToString("D");
            try
            {
                await joint.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected a changed server identity to reject the original plan.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-server-changed") { }
            finally { factory.SubstituteServerIdentity = null; }
            Require(!applications.IsRegistered(new AppId(AppIdText))
                && ModulePackageOperationStore.Read(serverRoot).Count == 0
                && await new FileDeploymentJournal(journalPath).FindStartedPlanAsync(review.Plan.Id) is null,
                "a different server identity at the same address rejects the old plan before creating a checkpoint or applying it");
            await ExpectRestartAsync(joint, review);
            Require(!applications.IsRegistered(new AppId(AppIdText)) && InstalledModuleStore.Read(serverRoot).Single().ModuleId == ModuleId,
                "the first execution installs the signed module and leaves the desktop package pending");

            factory.SubstituteReceiptRelease = true;
            try
            {
                await joint.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected the substituted receipt to be rejected.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-module-receipt-unavailable") { }
            finally { factory.SubstituteReceiptRelease = false; }
            Require(!applications.IsRegistered(new AppId(AppIdText)) && factory.SubmittedEnableApplies == 0,
                "a receipt with a mismatched release cannot enable the module or install the application");

            await host.DisposeAsync();
            host = null;
            host = await TestHost.StartAsync(serverRoot, config);
            session.Url = host.Url;
            joint = Service();
            session.Online = false;
            session.KeepDisconnectedIdentity = true;
            Require(await joint.FindPendingAsync(candidate, default) is null,
                "a disconnected client cannot recover a server plan using cached account and service IDs");
            session.KeepDisconnectedIdentity = false;
            session.Online = true;
            var originalAccount = session.AccountId;
            var requestsBeforeAccountChange = factory.Requests;
            session.AccountId = Guid.NewGuid();
            try
            {
                Require(await joint.FindPendingAsync(candidate, default) is null,
                    "a different account on the same connection cannot find the original pending plan");
                try
                {
                    await joint.ExecuteAsync(review, default);
                    throw new InvalidOperationException("Expected the other account to be unable to execute the original review.");
                }
                catch (InvalidOperationException error) when (error.Message == "deployment-account-changed") { }
                Require(factory.Requests == requestsBeforeAccountChange,
                    "the rejected account does not send any module requests");
            }
            finally { session.AccountId = originalAccount; }
            var originalConnection = session.ConnectionIdentity;
            session.ConnectionIdentity = "different-joint-test-server";
            try
            {
                Require(await joint.FindPendingAsync(candidate, default) is null,
                    "the same account on a different connection cannot find the pending plan");
                Require((await joint.RecoverArchivesAsync([], default)).Count == 0,
                    "the recovery queue does not surface archives from a different connection");
                Require(factory.Requests == requestsBeforeAccountChange,
                    "looking for recovery on the other connection does not send module requests");
            }
            finally { session.ConnectionIdentity = originalConnection; }
            review = await joint.FindPendingAsync(candidate, default) ?? throw new InvalidOperationException("Missing pending deployment.");
            using (var cancellation = new CancellationTokenSource())
            {
                var entered = factory.HoldNextEnableApply();
                var execution = joint.ExecuteAsync(review, cancellation.Token);
                await entered.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                try
                {
                    await execution.WaitAsync(TimeSpan.FromSeconds(10));
                    throw new InvalidOperationException("Expected the blocked enable request to be cancelled.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            Require(factory.SubmittedEnableApplies == 0 && !applications.IsRegistered(new AppId(AppIdText)),
                "cancelling an in-flight enable request leaves the application pending without submitting enable");
            factory.DropNextEnableResponse();
            await ExpectInterruptedEnableAsync(joint, review);
            Require(factory.EnablePreviewKeys.Distinct(StringComparer.Ordinal).Count() == 2,
                "retry after an interrupted enable requests a fresh server preview");
            await ExpectRestartAsync(joint, review);
            Require(factory.EnablePreviewKeys.Count == 2 && factory.SubmittedEnableApplies == 1,
                "a lost enable response is reconciled from the pending server state without submitting enable again");
            Require(!applications.IsRegistered(new AppId(AppIdText)), "the desktop stays pending while module enable awaits restart");

            await host.DisposeAsync();
            host = null;
            host = await TestHost.StartAsync(serverRoot, config);
            session.Url = host.Url;
            joint = Service();
            review = await joint.FindPendingAsync(candidate, default) ?? throw new InvalidOperationException("Missing deployment after enable.");
            var failureJournal = new FileDeploymentJournal(Path.Combine(journalPath, "failed-package"));
            var failureService = new JointAppDeploymentService(factory, auth, installer, packages, applications, failureJournal);
            await using (var lease = await failureJournal.OpenAsync(review.Plan, default))
                await lease.SaveAsync(new DeploymentCheckpoint(0, false), default);
            factory.SubstituteFailedPackage = true;
            try
            {
                await failureService.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected a failed package receipt to require a new review.");
            }
            catch (ModulePackageDeploymentFailedException error) when (error.ModuleId == ModuleId
                && error.OperationId != Guid.Empty && error.Message == "fixture-package-failure") { }
            finally { factory.SubstituteFailedPackage = false; }
            var reopenedFailure = new JointAppDeploymentService(factory, auth, installer, packages, applications,
                new FileDeploymentJournal(Path.Combine(journalPath, "failed-package")));
            var recoveredFailure = await reopenedFailure.RecoverArchivesAsync([], default);
            Require(!applications.IsRegistered(new AppId(AppIdText))
                && await reopenedFailure.FindPendingAsync(candidate, default) is null
                && recoveredFailure is [{ IsRecoveredDeployment: true, RequiresDeploymentReview: true } recovered]
                && recovered.ExpectedSha256 == review.Plan.OrderedReleases[^1].ArtifactIdentity,
                "a terminal package failure recovers the exact desktop archive for review instead of resuming execution");
            factory.DropDependencyReply = true;
            factory.BlockDependencyRelease = true;
            try
            {
                await joint.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected the dependency registration reply to be lost.");
            }
            catch (HttpRequestException error) when (error.Message == "fixture-dependency-reply-lost") { }
            var abandonedRelease = drive.ResolveRootChild($"System/desktop-module-releases/{review.Plan.Id:N}.json");
            Require(!applications.IsRegistered(new AppId(AppIdText)) && File.Exists(abandonedRelease),
                "a lost dependency reply leaves an uncommitted application and a durable release record");
            factory.BlockDependencyRelease = false;
            await packages.LoadInstalledAsync();
            Require(!File.Exists(abandonedRelease), "startup reconciles the uncommitted dependency before retry");
            factory.BlockHealthWhen = () => applications.IsRegistered(new AppId(AppIdText));
            try
            {
                await joint.ExecuteAsync(review, default);
                throw new InvalidOperationException("Expected final module health to prevent deployment completion.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-module-unhealthy") { }
            finally { factory.BlockHealthWhen = null; }
            var committedPath = packages.FindInstalled(AppIdText)?.InstallationPath;
            await using (var lease = await new FileDeploymentJournal(journalPath).OpenAsync(review.Plan, default))
                Require(committedPath is not null && lease.Checkpoint is { Verified: false, RequiresReview: false } pending
                    && pending.CompletedSteps == review.Plan.OrderedReleases.Length,
                    "failed final health leaves the committed application awaiting verification");
            joint = Service();
            var resumedCandidate = (await joint.RecoverArchivesAsync([], default))
                .Single(item => item.ExpectedSha256 == review.Plan.OrderedReleases[^1].ArtifactIdentity);
            using (var uiServices = new ServiceCollection().AddSingleton(localization).BuildServiceProvider())
            {
                typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, uiServices);
                using var model = new AppInstallerViewModel(installer)
                {
                    JointDeployment = joint,
                    CurrentPackage = resumedCandidate,
                    ConfirmDeploymentAsync = plan => Task.FromResult(plan.Id == review.Plan.Id),
                };
                Require(resumedCandidate.IsRepair && model.ActionLabel == localization.Get("app_installer.deployment.resume", ""),
                    "the installer offers to resume an already committed deployment");
                await model.InstallCommand.ExecuteAsync(null);
                var current = packages.FindInstalled(AppIdText);
                Require(current is not null && current.InstallationPath == committedPath && model.CurrentPackage is null
                    && model.Status.Resolve() == string.Format(localization.Get("app_installer.status.installed", ""),
                        current.DisplayName, current.Version),
                    "resuming the final health check preserves the installation and reports the original install");
            }
            var installed = packages.FindInstalled(AppIdText)!;
            var executor = new ModuleDeploymentExecutor(new(factory.CreateClient("joint-fixture"), auth));
            var moduleRelease = review.Plan.OrderedReleases.Single(item => item.Package.Id == ModuleId);
            factory.FaultedModule = ModuleId;
            try
            {
                await executor.VerifyAsync(review.Plan, moduleRelease, default);
                throw new InvalidOperationException("Expected module recovery to block joint deployment verification.");
            }
            catch (ModuleDeploymentRecoveryException error) when (error.ModuleId == ModuleId) { }
            finally { factory.FaultedModule = null; }
            factory.SubstituteHealthVersion = true;
            try
            {
                await executor.VerifyAsync(review.Plan, moduleRelease, default);
                throw new InvalidOperationException("Expected mismatched running version to be rejected.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-module-unhealthy") { }
            finally { factory.SubstituteHealthVersion = false; }
            await executor.VerifyAsync(review.Plan, moduleRelease, default);
            factory.SubstituteInstalledDigest = true;
            try
            {
                await executor.VerifyAsync(review.Plan, moduleRelease, default);
                throw new InvalidOperationException("Expected changed installed package identity to be rejected.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-module-not-effective") { }
            finally { factory.SubstituteInstalledDigest = false; }
            factory.SubstituteBlockedHealth = true;
            try
            {
                var report = await new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth).CheckHealthAsync();
                Require(report.Modules.Single(item => item.ModuleId == ModuleId) is
                    { Status: "blocked", ProblemCode: "module-host-code-trust-required" },
                    "the desktop accepts a valid health report containing a blocked ModuleHost");
            }
            finally { factory.SubstituteBlockedHealth = false; }
            try
            {
                await joint.ExecuteAsync(review with
                {
                    Candidate = review.Candidate with { ExpectedSha256 = new string('0', 64) },
                }, default);
                throw new InvalidOperationException("Expected the completed plan to reject a different candidate.");
            }
            catch (InvalidOperationException error) when (error.Message == "deployment-application-plan-changed") { }
            Require(await joint.ExecuteAsync(review, default) == installed,
                "completed deployment replay accepts the confirmed application archive identity");
            using var ready = new HttpClient { BaseAddress = new Uri(host.Url) };
            ready.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
            var health = await ready.GetFromJsonAsync<ServerModuleHealthPageDto>(
                ServerModuleManagementRoutes.ModuleHealth, RelaxKonOSJsonOptions.Default);
            Require(installed.Id == AppIdText && applications.IsRegistered(new AppId(AppIdText))
                && health?.Modules.Single(item => item.ModuleId == ModuleId) is { Enabled: true, Version: "1.0.0", Status: "healthy" }
                && ModulePackageOperationStore.Read(serverRoot).Count == 1
                && await ready.GetStringAsync("/api/v1.0/fixture-module/ping") == "pong"
                && await ready.GetStringAsync("/fixture-core") == "ready"
                && await joint.FindPendingAsync(candidate, default) is null,
                "the resumed deployment verifies the running module, Core and desktop application registration");
            using (var readinessServices = new ServiceCollection().AddSingleton(packages).AddSingleton(applications)
                .AddSingleton<IHttpClientFactory>(factory).BuildServiceProvider())
            using (var readiness = new ApplicationAvailabilityService(auth, readinessServices, null!))
            {
                var manifest = applications.GetManifest(new AppId(AppIdText))!;
                var requestsBeforeReadiness = factory.Requests;
                factory.BeforeNextClient = () => session.Url = host.Url;
                await readiness.RefreshAsync(manifest);
                Require(factory.Requests == requestsBeforeReadiness,
                    "launch readiness does not query modules using a replacement login");
                Require(await readiness.RefreshAsync(manifest) is { CanOpen: true }, "a healthy module satisfies launch readiness");
                factory.HideEffectiveVersion = true;
                Require(await readiness.RefreshAsync(manifest) is
                    { CanOpen: false, State: ComponentAvailabilityKind.Unknown, ComponentId: ModuleId },
                    "an installed independent module without a running version blocks launch without reporting it missing");
                factory.HideEffectiveVersion = false;
                var moduleCatalog = new ModuleDeploymentCatalog(new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth));
                var healthy = await moduleCatalog.ReadAsync(default);
                Require(healthy.Releases.Single(release => release.Package.Id == ModuleId && release.Satisfied)
                    .Capabilities.GetValueOrDefault(ServerCapabilities.Metrics) == "2",
                    "the signed package capability contract is preserved by installed module inventory");
                factory.SubstituteCapabilities = true;
                Require(await moduleCatalog.ObserveAsync(default) != healthy.Target,
                    "changing only the module's advertised capabilities invalidates the deployment target");
                var advertised = await moduleCatalog.ReadAsync(default);
                var versioned = advertised.Releases.Single(release => release.Package.Id == ModuleId && release.Satisfied);
                Require(versioned.Capabilities.GetValueOrDefault("fixture.changed") == "2",
                    "the effective candidate preserves the explicit capability contract version");
                factory.ObservedCapabilityVersion = "3";
                Require(await moduleCatalog.ObserveAsync(default) != advertised.Target,
                    "changing only a capability contract version invalidates the deployment target");
                var versionExecutor = new ModuleDeploymentExecutor(new(factory.CreateClient("joint-fixture"), auth));
                try
                {
                    await versionExecutor.VerifyAsync(review.Plan, versioned, default);
                    throw new InvalidOperationException("Expected exact capability contract verification to fail.");
                }
                catch (InvalidOperationException error) when (error.Message == "deployment-module-not-effective") { }
                factory.ObservedCapabilityVersion = "2";
                await versionExecutor.VerifyAsync(review.Plan, versioned, default);
                factory.SubstituteCapabilities = false;
                factory.SubstituteInstalledDigest = true;
                Require(await moduleCatalog.ObserveAsync(default) != healthy.Target,
                    "changing only the active installed archive invalidates the deployment target");
                factory.SubstituteInstalledDigest = false;
                factory.SubstituteInventoryDigest = true;
                var mismatched = await moduleCatalog.ReadAsync(default);
                Require(!mismatched.Releases.Any(release => release.Package.Id == ModuleId && release.Satisfied),
                    "a running independent module with a mismatched installed digest is not reusable");
                factory.SubstituteInventoryDigest = false;
                factory.FaultedModule = ModuleId;
                Require(await readiness.RefreshAsync(manifest) is
                    { CanOpen: false, State: ComponentAvailabilityKind.Recovery, ComponentId: ModuleId },
                    "a faulted module cannot satisfy launch readiness even when effectively enabled");
                var faulted = await moduleCatalog.ReadAsync(default);
                Require(healthy.Releases.Any(release => release.Package.Id == ModuleId && release.Satisfied)
                    && faulted.Releases.All(release => release.Package.Id != ModuleId || !release.Satisfied)
                    && healthy.Target.StateRevision != faulted.Target.StateRevision,
                    "a faulted module invalidates the deployment snapshot and is never a satisfied candidate");
                factory.FaultedModule = null;
            }
            var protectionClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
            var protectedInventory = await protectionClient.ReadAsync();
            var disableProtected = await protectionClient.PreviewAsync(new(serviceId, ServerModuleAction.Disable, [ModuleId],
                protectedInventory.Where(module => module.DesiredEnabled && module.Id != ModuleId).Select(module => module.Id).ToArray(),
                protectedInventory[0].DesiredRevision!, "verify-registered-dependency"));
            Require(disableProtected.BlockedBy.Any(blocker => blocker.ProblemCode == "module-desktop-dependency-blocked" && blocker.ModuleId == ModuleId),
                "committing the application registers a server-side blocker for its required module");
            var upgradeSource = Path.Combine(root, "versioned-upgrade-source.roapp");
            File.Copy(packagePath, upgradeSource);
            await RewriteDesktopArchiveAsync(upgradeSource, packagePath, manifest => manifest["version"] = "1.1.0");
            await using (var upgradedArchive = File.OpenRead(packagePath))
                await File.WriteAllTextAsync(packagePath + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(upgradedArchive)));
            var upgrade = await joint.PrepareAsync(await installer.InspectLocalAsync(packagePath), [], default);
            Require((await joint.ExecuteAsync(upgrade, default)).Version == "1.1.0",
                "upgrading the desktop application commits a replacement module dependency");
            factory.DropDependencyReply = true;
            factory.BlockDependencyRelease = true;
            var interruptedRollback = await packages.SwitchAsync(AppIdText, rollback: true, launch: false, default);
            Require(!interruptedRollback.Ok && packages.FindInstalled(AppIdText)?.Version == "1.1.0"
                && File.Exists(abandonedRelease),
                $"an interrupted rollback leaves the current version and queues its uncommitted dependency ({interruptedRollback.Code}, {packages.FindInstalled(AppIdText)?.Version}, queued={File.Exists(abandonedRelease)})");
            factory.BlockDependencyRelease = false;
            await packages.LoadInstalledAsync();
            Require(!File.Exists(abandonedRelease), "startup reconciles the uncommitted rollback dependency");
            Require((await packages.SwitchAsync(AppIdText, rollback: true, launch: false, default)).Ok
                && packages.FindInstalled(AppIdText)?.Version == installed.Version,
                "rolling back the desktop application restores its original module dependency before switching");

            var secondWindows = new RelaxKonOS.WindowManager.WindowManager();
            var secondApplications = new ApplicationManager(secondWindows, services);
            var secondDrive = new VirtualSystemDrive(Path.Combine(root, "second-desktop-drive"));
            var secondPackages = new DeveloperPackageManager(secondApplications, null!, secondWindows, permissions,
                new Diagnostics(), secondDrive, official, moduleHttp: factory, moduleSession: auth);
            await secondPackages.LoadInstalledAsync();
            var secondInstaller = new AppPackageInstallerService(secondPackages, null!, trust);
            var secondCoordinator = new JointAppDeploymentService(factory, auth, secondInstaller, secondPackages,
                secondApplications, new(Path.Combine(root, "second-desktop-journal")));
            var secondReview = await secondCoordinator.PrepareAsync(await secondInstaller.InspectLocalAsync(packagePath), [], default);
            Require(secondReview.Plan.Id != review.Plan.Id
                && (await secondCoordinator.ExecuteAsync(secondReview, default)).Version == "1.1.0",
                "a second desktop under the same account independently registers the same module dependency");

            session.Online = false;
            await packages.UninstallAsync(AppIdText);
            var releaseDirectory = drive.ResolveRootChild("System/desktop-module-releases");
            Require(Directory.EnumerateFiles(releaseDirectory, "*.json").Any(),
                "offline desktop uninstall queues its server dependency release");
            session.Online = true;
            var sharedClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
            var sharedInventory = await sharedClient.ReadAsync();
            var sharedDisable = new ServerModulePlanRequest(serviceId, ServerModuleAction.Disable, [ModuleId],
                sharedInventory.Where(module => module.DesiredEnabled && module.Id != ModuleId).Select(module => module.Id).ToArray(),
                sharedInventory[0].DesiredRevision!, "second-desktop-still-installed");
            Require((await sharedClient.PreviewAsync(sharedDisable with { IdempotencyKey = "offline-release-queued" })).BlockedBy.Any(blocker =>
                    blocker.ProblemCode == "module-desktop-dependency-blocked" && blocker.ModuleId == ModuleId),
                "the server still blocks disable while the offline release is queued");
            Require(await secondPackages.UninstallAsync(AppIdText),
                "the second desktop can uninstall and release its claim while the first release is queued");
            var secondReleaseDirectory = secondDrive.ResolveRootChild("System/desktop-module-releases");
            Require(!Directory.Exists(secondReleaseDirectory) || !Directory.EnumerateFiles(secondReleaseDirectory, "*.json").Any(),
                "the online uninstall leaves no deferred release on the second desktop");
            Require((await sharedClient.PreviewAsync(sharedDisable with { IdempotencyKey = "only-offline-release-queued" })).BlockedBy.Any(blocker =>
                    blocker.ProblemCode == "module-desktop-dependency-blocked" && blocker.ModuleId == ModuleId),
                "the unreported offline uninstall alone keeps module protection in force");
            await packages.LoadInstalledAsync();
            Require(!Directory.EnumerateFiles(releaseDirectory, "*.json").Any(),
                "startup releases an offline uninstall record on the original server and account");
            Require((await sharedClient.PreviewAsync(sharedDisable with { IdempotencyKey = "both-claims-released" })).BlockedBy.Count == 0,
                "reconciling the last claim allows the module to be disabled");

            await installer.InstallAsync(await installer.InspectLocalAsync(prerequisitePath));
            var replacementMixed = await joint.PrepareAsync(mixedCandidate, [], default);
            Require(replacementMixed.Plan.Id != mixed.Plan.Id
                && replacementMixed.Plan.OrderedReleases.Take(2).All(release => release.Satisfied),
                "reviewing again after restoring the local prerequisite reuses the now healthy module");
            var mixedInstalled = await joint.ExecuteAsync(replacementMixed, default);
            Require(mixedInstalled.Id == mixedCandidate.Manifest.Id
                && applications.IsRegistered(new AppId(mixedInstalled.Id))
                && ModulePackageOperationStore.Read(serverRoot).Count == 1
                && !(await reopenedMixedJournal.ReadPendingRecoveriesAsync(auth.CurrentUser!.Id, auth.ServiceId!))
                    .Any(recovery => recovery.Plan.OrderedReleases[^1].Package.Id == mixedInstalled.Id),
                "completing the replacement plan retires the old recovery entry without installing the module again");
            await packages.UninstallAsync(mixedInstalled.Id);
            await packages.UninstallAsync(prerequisiteId);

            var activationArchives = bundledModuleArchives.Where(path =>
                Path.GetFileNameWithoutExtension(path) is ServerModules.FirewallId or ServerModules.GitId
                    or ServerModules.DatabaseId or ServerModules.DockerId or "relaxkonos.webservers")
                .ToDictionary(path => Path.GetFileNameWithoutExtension(path) == "relaxkonos.webservers"
                    ? ServerModules.WebServerId : Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);
            var activationDependencies = new[] { ServerModules.FirewallId, ModuleId }
                .Concat(new[] { ServerModules.GitId, ServerModules.DatabaseId, ServerModules.DockerId, ServerModules.WebServerId }
                    .Where(activationArchives.ContainsKey));
            foreach (var dependencyId in activationDependencies)
            {
                var moduleClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                var inventory = await moduleClient.ReadAsync();
                var enabled = inventory.Where(module => module.DesiredEnabled && module.Id != dependencyId)
                    .Select(module => module.Id).Order(StringComparer.Ordinal).ToArray();
                var disable = await moduleClient.PreviewAsync(new(serviceId, ServerModuleAction.Disable,
                    [dependencyId], enabled, inventory[0].DesiredRevision!, "disable-for-joint-test-" + dependencyId));
                Require(disable.BlockedBy.Count == 0,
                    $"the isolated host previews disabling {dependencyId}: "
                    + string.Join(", ", disable.BlockedBy.Select(item => $"{item.ProblemCode}/{item.ModuleId}")));
                var disabled = await moduleClient.ApplyAsync(disable.PlanId);
                Require(disabled.State == ServerModuleOperationState.AwaitingRestart,
                    $"the isolated host accepts disabling {dependencyId} ({disabled.State}/{disabled.ProblemCode})");
                await host.DisposeAsync();
                host = await TestHost.StartAsync(serverRoot, config);
                session.Url = host.Url;
                joint = Service();
                var restartedClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                var stoppedInventory = await restartedClient.ReadAsync();
                var stopped = stoppedInventory.Single(module => module.Id == dependencyId);
                var stopReceipt = await restartedClient.OperationAsync(disabled.OperationId);
                Require(!stopped.DesiredEnabled && !stopped.EffectiveEnabled && !stopped.RestartRequired
                    && stopReceipt.State == ServerModuleOperationState.Succeeded,
                    $"restart completes the disable receipt and makes {dependencyId} ineffective");

                var officialArchive = activationArchives.GetValueOrDefault(dependencyId);
                var activationAppId = officialArchive is null ? "fixture.bundled" : Path.GetFileNameWithoutExtension(officialArchive);
                var activationArchive = officialArchive is null
                    ? Path.Combine(root, dependencyId + "-dependent.roapp")
                    : Path.Combine(official, activationAppId + ".roapp");
                if (officialArchive is not null)
                {
                    File.Copy(officialArchive, activationArchive);
                    File.Copy(officialArchive + ".sha256", activationArchive + ".sha256");
                }
                else
                    await PrepareDesktopArchiveAsync(archive, activationArchive, activationAppId,
                        new DeploymentPackageKey(DeploymentPackageKind.ServerModule, dependencyId));
                var activationCandidate = await installer.InspectLocalAsync(activationArchive);
                var activationReview = await joint.PrepareAsync(activationCandidate, [], default);
                Require(activationReview.Plan.PreparedSteps.IsEmpty &&
                    activationReview.Plan.OrderedReleases.Single(release => release.ActivationOnly)
                        is { Satisfied: false } activation && activation.Package.Id == dependencyId,
                    $"{dependencyId} is reviewed as enable-only without a package download");
                if (dependencyId == ServerModules.WebServerId)
                    Require(activationReview.Plan.OrderedReleases.Select(release => release.Package.Id)
                            .SequenceEqual([ServerModules.CertificatesId, ServerModules.WebServerId, activationAppId])
                        && activationReview.Plan.OrderedReleases[0].Satisfied,
                        "the Web Server Manager review reuses Certificates before activating Web Servers");
                await ExpectRestartAsync(joint, activationReview);
                Require(!applications.IsRegistered(new AppId(activationAppId))
                    && ModulePackageOperationStore.Read(serverRoot).Count == 1,
                    $"enabling {dependencyId} leaves the application pending without a package operation");
                await host.DisposeAsync();
                host = await TestHost.StartAsync(serverRoot, config);
                session.Url = host.Url;
                joint = Service();
                var activeClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                var effective = (await activeClient.ReadAsync()).Single(module => module.Id == dependencyId);
                Require(effective.DesiredEnabled && effective.EffectiveEnabled && !effective.RestartRequired
                    && effective.RecoveryState == "none" && !applications.IsRegistered(new AppId(activationAppId)),
                    $"restart makes {dependencyId} effective before the desktop application is registered");
                var activationPending = await joint.FindPendingAsync(activationCandidate, default)
                    ?? throw new InvalidOperationException("Missing activation-only deployment after restart.");
                var activationInstalled = await joint.ExecuteAsync(activationPending, default);
                Require(activationInstalled.Id == activationAppId && applications.IsRegistered(new AppId(activationAppId)),
                    $"restart activates {dependencyId} and permits desktop application registration");
                await packages.UninstallAsync(activationAppId);
            }

            var proxyDependents = new List<string>();
            foreach (var bundledArchive in bundledModuleArchives)
            {
                var appId = Path.GetFileNameWithoutExtension(bundledArchive);
                string[] expectedModules = appId switch
                {
                    "relaxkonos.certificates" => [ServerModules.CertificatesId],
                    "relaxkonos.webservers" => [ServerModules.CertificatesId, ServerModules.WebServerId],
                    "relaxkonos.git" => [ServerModules.GitId],
                    "relaxkonos.firewall" => [ServerModules.FirewallId],
                    "relaxkonos.database" => [ServerModules.DatabaseId],
                    "relaxkonos.docker" => [ServerModules.DockerId],
                    "relaxkonos.proxy" or "relaxkonos.proxy-server" or "relaxkonos.squid" => [ServerModules.ProxyId],
                    _ => throw new ArgumentException($"Unrecognized bundled-module test package: {appId}"),
                };
                var bundledPath = Path.Combine(official, appId + ".roapp");
                File.Copy(bundledArchive, bundledPath, overwrite: true);
                File.Copy(bundledArchive + ".sha256", bundledPath + ".sha256", overwrite: true);
                var bundledCandidate = await installer.InspectLocalAsync(bundledPath);
                var bundledReview = await joint.PrepareAsync(bundledCandidate, [], default);
                var releases = bundledReview.Plan.OrderedReleases;
                Require(bundledReview.Plan.PreparedSteps.IsEmpty && releases.Length == expectedModules.Length + 1
                    && releases[^1].Package == new DeploymentPackageKey(DeploymentPackageKind.Application, appId)
                    && releases.Take(expectedModules.Length).All(item =>
                        item.Package.Kind == DeploymentPackageKind.ServerModule && item.Satisfied)
                    && releases.Take(expectedModules.Length).Select(item => item.Package.Id).SequenceEqual(expectedModules),
                    $"{appId} reuses every effective server dependency in dependency order");
                if (appId == "relaxkonos.git")
                {
                    const string version = RelaxKonOS.Protocol.Git.GitApiRoutes.CapabilityContractVersion;
                    Require(bundledCandidate.Manifest.Dependencies?.Required.Any(requirement =>
                            requirement.Capability == ServerCapabilities.Git && requirement.ContractVersion == version) == true
                        && releases[0].Capabilities.GetValueOrDefault(ServerCapabilities.Git) == version,
                        "the official Project Editor archive resolves the bundled Git capability contract");
                }
                var operationsBefore = ModulePackageOperationStore.Read(serverRoot).Count;
                var installedBundled = await joint.ExecuteAsync(bundledReview, default);
                Require(installedBundled.Id == appId && applications.IsRegistered(new AppId(appId))
                    && ModulePackageOperationStore.Read(serverRoot).Count == operationsBefore,
                    $"{appId} installs through the coordinator without mutating existing modules");
                if (expectedModules.Contains(ServerModules.ProxyId))
                {
                    proxyDependents.Add(appId);
                }
                else
                {
                    var directModules = appId == "relaxkonos.webservers" ? [ServerModules.WebServerId] : expectedModules;
                    var dependencies = directModules.Select(id => new DeploymentPackageKey(DeploymentPackageKind.ServerModule, id)).ToArray();
                    Require(dependencies.All(key => packages.RequiredBy(key).Contains(appId)),
                        $"{appId} is recorded as a dependent of each directly required module");
                    if (appId == "relaxkonos.webservers")
                    {
                        var moduleClient = new ServerModuleManagementClient(factory.CreateClient("joint-fixture"), auth);
                        var inventory = await moduleClient.ReadAsync();
                        var desired = inventory.Where(module => module.DesiredEnabled && module.Id != ServerModules.CertificatesId)
                            .Select(module => module.Id).Order(StringComparer.Ordinal).ToArray();
                        var disable = await moduleClient.PreviewAsync(new(serviceId, ServerModuleAction.Disable,
                            [ServerModules.CertificatesId], desired, inventory[0].DesiredRevision!, "verify-webserver-certificate-protection"));
                        Require(disable.BlockedBy.Any(blocker => blocker.ProblemCode == "module-dependency-blocked"
                            && blocker.ModuleId == ServerModules.CertificatesId),
                            "the server protects Certificates while Web Servers requires it");
                    }
                    await packages.UninstallAsync(appId);
                    Require(dependencies.All(key => !packages.RequiredBy(key).Contains(appId))
                        && ModulePackageOperationStore.Read(serverRoot).Count == operationsBefore,
                        $"uninstalling {appId} removes its dependency without changing the reused modules");
                }
            }

            var proxyModule = new DeploymentPackageKey(DeploymentPackageKind.ServerModule, ServerModules.ProxyId);
            if (proxyDependents.Count > 0)
            {
                Require(packages.RequiredBy(proxyModule).SequenceEqual(proxyDependents.Order(StringComparer.Ordinal)),
                    "every installed Proxy application protects the shared server module");
                foreach (var appId in proxyDependents)
                {
                    await packages.UninstallAsync(appId);
                    var remaining = proxyDependents.SkipWhile(item => item != appId).Skip(1).Order(StringComparer.Ordinal);
                    Require(packages.RequiredBy(proxyModule).SequenceEqual(remaining),
                        $"uninstalling {appId} preserves the shared dependency of the remaining applications");
                }
            }

            // Use the original desktop archive as a local prerequisite without a server dependency.
            File.Copy(archive, packagePath, overwrite: true);
            await using (var file = File.OpenRead(packagePath))
                await File.WriteAllTextAsync(packagePath + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
            await installer.InstallAsync(await installer.InspectLocalAsync(packagePath));
            var offlineArchive = Path.Combine(root, "dependent.roapp");
            await PrepareDesktopArchiveAsync(archive, offlineArchive, "fixture.offline",
                new DeploymentPackageKey(DeploymentPackageKind.Application, AppIdText));
            session.Online = false;
            var requests = factory.Requests;
            var offlineCandidate = await installer.InspectLocalAsync(offlineArchive);
            var offline = await joint.PrepareAsync(offlineCandidate, [], default);
            var offlineApp = await joint.ExecuteAsync(offline, default);
            Require(offlineApp.Id == "fixture.offline" && factory.Requests == requests
                && offline.Plan.OrderedReleases[0].Satisfied,
                "a real desktop package with a satisfied client-only dependency installs offline without module API calls");
            using var availabilityServices = new ServiceCollection().AddSingleton(packages).AddSingleton(applications).BuildServiceProvider();
            using var availability = new ApplicationAvailabilityService(auth, availabilityServices, null!);
            Require(availability.GetCached(new AppId(offlineApp.Id)) is
                    { CanOpen: true, PackageKind: DeploymentPackageKind.Application, ServerIdentity: null }
                && factory.Requests == requests,
                "the offline desktop reports a satisfied local dependency without making a module API call");
            var original = packages.FindInstalled(AppIdText);
            await RewriteDesktopArchiveAsync(archive, packagePath, manifest => manifest["version"] = "2.0.0");
            var blocked = false;
            try { await installer.InstallAsync(await installer.InspectLocalAsync(packagePath)); }
            catch (InvalidOperationException error) when (error.Message.Contains("fixture.offline", StringComparison.Ordinal))
            {
                blocked = true;
            }
            Require(blocked && packages.FindInstalled(AppIdText) == original,
                "an incompatible replacement preserves the app version required by another installed app");
            await packages.UninstallAsync("fixture.offline");
            var upgraded = await installer.InstallAsync(await installer.InspectLocalAsync(packagePath));
            Require(upgraded.Version == "2.0.0", "removing the consuming app lets the same replacement proceed");
            await packages.UninstallAsync(AppIdText);

        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            if (Path.GetDirectoryName(root) == parent)
            {
                // Windows can retain the app's loaded package files until the process exits.
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static Task PrepareDesktopArchiveAsync(string source, string path,
        string appId = AppIdText, DeploymentPackageKey? dependency = null, string? optionalModuleId = null,
        string? capability = null, string? contractVersion = null) =>
        RewriteDesktopArchiveAsync(source, path, manifest =>
        {
            var package = dependency ?? new DeploymentPackageKey(DeploymentPackageKind.ServerModule, ModuleId);
            manifest["id"] = appId;
            manifest["dependencies"] = new JsonObject
            {
                ["required"] = new JsonArray(new JsonObject
                {
                    ["package"] = new JsonObject { ["kind"] = (int)package.Kind, ["id"] = package.Id },
                    ["minimumVersion"] = "1.0.0",
                    ["maximumVersionExclusive"] = "2.0.0",
                }),
            };
            if (capability is not null)
            {
                var required = manifest["dependencies"]!["required"]![0]!;
                required["capability"] = capability;
                required["contractVersion"] = contractVersion;
            }
            if (optionalModuleId is not null)
                manifest["dependencies"]!["optional"] = new JsonArray(new JsonObject
                {
                    ["package"] = new JsonObject { ["kind"] = (int)DeploymentPackageKind.ServerModule, ["id"] = optionalModuleId },
                });
        });

    private static async Task RewriteDesktopArchiveAsync(string source, string path, Action<JsonNode> edit)
    {
        File.Copy(source, path, overwrite: true);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (var input = entry.Open()) manifest = JsonNode.Parse(input)!;
            edit(manifest);
            entry.Delete();
            using var output = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            await output.WriteAsync(manifest.ToJsonString());
        }
        await using var file = File.OpenRead(path);
        await File.WriteAllTextAsync(path + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
    }

    private static async Task ExpectRestartAsync(JointAppDeploymentService service, JointAppDeploymentService.Review review)
    {
        try { await service.ExecuteAsync(review, default); }
        catch (InvalidOperationException error) when (error.Message == "deployment-module-AwaitingRestart") { return; }
        throw new InvalidOperationException("The installation should wait for a server restart.");
    }

    private static async Task ExpectInterruptedEnableAsync(JointAppDeploymentService service, JointAppDeploymentService.Review review)
    {
        try { await service.ExecuteAsync(review, default); }
        catch (HttpRequestException error) when (error.Message == "fixture-enable-apply-interrupted") { return; }
        throw new InvalidOperationException("Expected the enable request to be interrupted.");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class TestHost(WebApplication app, ModuleManagementStore store) : IAsyncDisposable
    {
        public string Url => app.Urls.Single();

        public static async Task<TestHost> StartAsync(string root, IConfiguration config)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root, EnvironmentName = Environments.Development, Args = [],
            });
            builder.Logging.ClearProviders();
            builder.Configuration.AddConfiguration(config);
            var installed = InstalledModuleCatalog.Load(root);
            var store = new ModuleManagementStore(builder.Configuration, root, installed);
            var modules = ServerModules.CreateManaged(store.DesiredIds, installed);
            var online = installed.Where(module => modules.IsEnabled(module.Id)).ToArray();
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            if (online.Any(module => module.Id == ServerModules.GitId))
            {
                builder.Services.AddSingleton(DispatchProxy.Create<RelaxKonOS.Server.Files.IFileService, GitRepositoryAssemblyChecks.RejectTransport>());
                ServerModules.CreateManaged(new HashSet<string> { ServerModules.GitId }, installed).ConfigureServices(builder, context);
            }
            foreach (var module in online.Where(module => module.Id != ServerModules.GitId)) module.ConfigureServices(builder, context);
            builder.Services.Configure<RelaxKonOS.Server.Docker.DockerComposeOptions>(options =>
                options.DataDirectory = Path.Combine(root, "docker-compose"));
            builder.Services.AddSingleton<RelaxKonOS.Server.Docker.DockerStackOperationStore>();
            builder.Services.AddSingleton<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport, IdleDatabaseHelper>();
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton(context);
            builder.Services.AddSingleton<ModuleManagementExecutor>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ModuleManagementExecutor>());
            builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy(ServerModuleManagementEndpoints.ManagePolicy,
                policy => policy.RequireAuthenticatedUser().RequireAssertion(user => ServerModuleManagementEndpoints.CanManage(user.User))));
            WebApplication? app = null;
            try
            {
                app = builder.Build();
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet(ServerApiRoutes.Modules, async (HttpContext http, CancellationToken ct) => Results.Ok(
                    (await modules.DescribeAsync(http.RequestServices, context, ct))
                        .Select(module => ServerModuleManagementEndpoints.ForActor(module, http.User)))).RequireAuthorization();
                app.MapGet("/fixture-core", () => "ready");
                app.MapServerModuleManagementEndpoints();
                foreach (var module in online) module.MapEndpoints(app, context);
                app.Urls.Add("http://127.0.0.1:0");
                await app.StartAsync();
                store.VerifyStartup(modules.EnabledIds);
                ModulePackageOperationStore.VerifyStartup(root, InstalledModuleStore.Read(root),
                    store.ProcessGeneration, DateTimeOffset.UtcNow);
                return new(app, store);
            }
            catch
            {
                try { if (app is not null) await app.DisposeAsync(); }
                finally { store.Dispose(); }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            store.Dispose();
        }
    }

    private sealed class FixtureHttpFactory(byte[] signedArchive) : IHttpClientFactory, IDisposable
    {
        private readonly ArchiveUploadHandler handler = new(signedArchive);
        private HttpClient? http;
        public IReadOnlyList<string> EnablePreviewKeys => handler.EnablePreviewKeys;
        public int SubmittedEnableApplies => handler.SubmittedEnableApplies;
        public int Requests => handler.Requests;
        public bool SubstituteReceiptRelease { get => handler.SubstituteReceiptRelease; set => handler.SubstituteReceiptRelease = value; }
        public bool SubstituteHealthVersion { get => handler.SubstituteHealthVersion; set => handler.SubstituteHealthVersion = value; }
        public bool SubstituteBlockedHealth { get => handler.SubstituteBlockedHealth; set => handler.SubstituteBlockedHealth = value; }
        public Func<bool>? BlockHealthWhen { set => handler.BlockHealthWhen = value; }
        public bool HideEffectiveVersion { get => handler.HideEffectiveVersion; set => handler.HideEffectiveVersion = value; }
        public bool SubstituteInventoryDigest { get => handler.SubstituteInventoryDigest; set => handler.SubstituteInventoryDigest = value; }
        public bool SubstituteFailedPackage { get => handler.SubstituteFailedPackage; set => handler.SubstituteFailedPackage = value; }
        public string? SubstituteServerIdentity { set => handler.SubstituteServerIdentity = value; }
        public bool SubstituteInstalledDigest { get => handler.SubstituteInstalledDigest; set => handler.SubstituteInstalledDigest = value; }
        public string? FaultedModule { get => handler.FaultedModule; set => handler.FaultedModule = value; }
        public bool SubstituteCapabilities { get => handler.SubstituteCapabilities; set => handler.SubstituteCapabilities = value; }
        public string ObservedCapabilityVersion { set => handler.ObservedCapabilityVersion = value; }
        public Action? BeforeNextClient { get; set; }
        public Func<Task>? BeforeNextRequest { set => handler.BeforeNextRequest = value; }
        public Task HoldNextEnableApply()
        {
            handler.EnableEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return handler.EnableEntered.Task;
        }
        public void DropNextEnableResponse() => handler.DropEnableResponse = true;
        public bool DropDependencyReply { set => handler.DropDependencyReply = value; }
        public bool BlockDependencyRelease { set => handler.BlockDependencyRelease = value; }
        public HttpClient CreateClient(string name)
        {
            var before = BeforeNextClient;
            BeforeNextClient = null;
            before?.Invoke();
            return http ??= new(handler);
        }
        public void Dispose() => http?.Dispose();
    }

    // Use the real server staging endpoint in place of the external HTTPS artifact host.
    private sealed class ArchiveUploadHandler(byte[] signedArchive) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        public List<string> EnablePreviewKeys { get; } = [];
        public TaskCompletionSource? EnableEntered { get; set; }
        public bool DropEnableResponse { get; set; }
        public bool DropDependencyReply { get; set; }
        public bool BlockDependencyRelease { get; set; }
        public bool SubstituteReceiptRelease { get; set; }
        public bool SubstituteHealthVersion { get; set; }
        public bool SubstituteBlockedHealth { get; set; }
        public Func<bool>? BlockHealthWhen { get; set; }
        public bool HideEffectiveVersion { get; set; }
        public bool SubstituteInventoryDigest { get; set; }
        public bool SubstituteFailedPackage { get; set; }
        public string? SubstituteServerIdentity { get; set; }
        public bool SubstituteInstalledDigest { get; set; }
        public int SubmittedEnableApplies { get; private set; }
        public int Requests { get; private set; }
        public string? FaultedModule { get; set; }
        public bool SubstituteCapabilities { get; set; }
        public string ObservedCapabilityVersion { get; set; } = "2";
        public Func<Task>? BeforeNextRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var before = BeforeNextRequest;
            BeforeNextRequest = null;
            if (before is not null) await before();
            request.Headers.Add("test-user", ModuleTestAuthentication.Admin);
            var path = request.RequestUri?.AbsolutePath;
            if (path?.StartsWith(ServerModuleManagementRoutes.DesktopDependencies + "/", StringComparison.Ordinal) == true)
            {
                if (request.Method == HttpMethod.Delete && BlockDependencyRelease)
                    throw new HttpRequestException("fixture-dependency-release-unreachable");
                if (request.Method == HttpMethod.Put && DropDependencyReply)
                {
                    DropDependencyReply = false;
                    using var applied = await base.SendAsync(request, cancellationToken);
                    applied.EnsureSuccessStatusCode();
                    throw new HttpRequestException("fixture-dependency-reply-lost");
                }
            }
            if (SubstituteInstalledDigest && request.Method == HttpMethod.Get && path == ServerModuleManagementRoutes.InstalledPackages)
            {
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var page = (await response.Content.ReadFromJsonAsync<ServerInstalledModulePageDto>(
                    RelaxKonOSJsonOptions.Default, cancellationToken))!;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(page with
                    {
                        Items = page.Items.Select(item => item.ModuleId == ModuleId
                            ? item with { ArtifactSha256 = new string('0', 64) } : item).ToArray(),
                    }, options: RelaxKonOSJsonOptions.Default),
                };
            }
            var blockedHealth = request.Method == HttpMethod.Get && path == ServerModuleManagementRoutes.ModuleHealth
                && (SubstituteBlockedHealth || BlockHealthWhen?.Invoke() == true);
            if ((SubstituteHealthVersion || blockedHealth)
                && request.Method == HttpMethod.Get && path == ServerModuleManagementRoutes.ModuleHealth)
            {
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var report = (await response.Content.ReadFromJsonAsync<ServerModuleHealthPageDto>(
                    RelaxKonOSJsonOptions.Default, cancellationToken))!;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(report with
                    {
                        Healthy = report.Healthy && !blockedHealth,
                        Modules = report.Modules.Select(item => item.ModuleId != ModuleId ? item
                            : blockedHealth ? item with
                            {
                                Status = "blocked", ProblemCode = "module-host-code-trust-required", Version = null,
                            }
                            : item with { Version = "9.0.0" }).ToArray(),
                    }, options: RelaxKonOSJsonOptions.Default),
                };
            }
            if ((SubstituteReceiptRelease || SubstituteFailedPackage) && request.Method == HttpMethod.Get
                && path?.StartsWith(ServerModuleManagementRoutes.PackageOperations + "/", StringComparison.Ordinal) == true)
            {
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var receipt = await response.Content.ReadFromJsonAsync<ServerPackageOperationDto>(
                    RelaxKonOSJsonOptions.Default, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(SubstituteFailedPackage
                        ? receipt! with { State = "Failed", ProblemCode = "fixture-package-failure" }
                        : receipt! with { ReleaseId = "other-release" }, options: RelaxKonOSJsonOptions.Default),
                };
            }
            if (request.Method == HttpMethod.Get && path == ServerApiRoutes.Modules
                && (FaultedModule is not null || HideEffectiveVersion || SubstituteCapabilities || SubstituteInventoryDigest || SubstituteServerIdentity is not null))
            {
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var modules = await response.Content.ReadFromJsonAsync<ServerModuleDto[]>(RelaxKonOSJsonOptions.Default, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(modules!.Select(module => SubstituteServerIdentity is { } identity
                        ? module with { ServiceId = identity }
                        : module.Id == FaultedModule
                        ? module with { Residency = new("faulted", false, "KeepLoaded", 0, 0, null, null, null, "fixture", null) }
                        : HideEffectiveVersion && module.Id == ModuleId ? module with { EffectiveVersion = null }
                        : SubstituteCapabilities && module.Id == ModuleId ? module with { Capabilities = ["fixture.changed"], CapabilityContractVersions =
                            new Dictionary<string, string> { ["fixture.changed"] = ObservedCapabilityVersion } }
                        : SubstituteInventoryDigest && module.Id == ModuleId ? module with { ArtifactDigest = new string('0', 64) }
                        : module).ToArray(), options: RelaxKonOSJsonOptions.Default),
                };
            }
            if (request.Method == HttpMethod.Post && path == ServerModuleManagementRoutes.Plans)
            {
                var plan = await request.Content!.ReadFromJsonAsync<ServerModulePlanRequest>(
                    RelaxKonOSJsonOptions.Default, cancellationToken);
                if (plan?.Action == ServerModuleAction.Enable) EnablePreviewKeys.Add(plan.IdempotencyKey);
            }
            if (request.Method == HttpMethod.Post
                && path?.StartsWith(ServerModuleManagementRoutes.Plans + "/", StringComparison.Ordinal) == true
                && path.EndsWith("/apply", StringComparison.Ordinal))
            {
                if (EnableEntered is { } entered)
                {
                    EnableEntered = null;
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                var drop = DropEnableResponse;
                DropEnableResponse = false;
                SubmittedEnableApplies++;
                var applied = await base.SendAsync(request, cancellationToken);
                if (!drop) return applied;
                using (applied) applied.EnsureSuccessStatusCode();
                throw new HttpRequestException("fixture-enable-apply-interrupted");
            }
            if (request.RequestUri?.AbsolutePath != ServerModuleManagementRoutes.ModuleStoreDownload)
                return await base.SendAsync(request, cancellationToken);
            using var upload = new HttpRequestMessage(HttpMethod.Post, new Uri(request.RequestUri, ServerModuleManagementRoutes.PackageUpload));
            upload.Headers.Add("test-user", ModuleTestAuthentication.Admin);
            upload.Content = new ByteArrayContent(signedArchive);
            return await base.SendAsync(upload, cancellationToken);
        }
    }

    public class SessionProxy : DispatchProxy
    {
        public Guid AccountId { get; set; } = Guid.Parse(ModuleTestAuthentication.Admin);
        private SessionDto login = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default, default,
            SessionStatus.Active, Guid.Parse(ModuleTestAuthentication.Admin), "test", default);
        private string url = "";
        public bool Online { get; set; } = true;
        public bool KeepDisconnectedIdentity { get; set; }
        public string ConnectionIdentity { get; set; } = "local-joint-test";
        public string Url
        {
            get => url;
            set
            {
                url = value;
                login = login with { Id = Guid.NewGuid() };
            }
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_State" => Online ? AuthSessionState.Authenticated : AuthSessionState.Unauthenticated,
            "get_ServiceId" => Online || KeepDisconnectedIdentity ? ConnectionIdentity : null,
            "get_EffectiveBaseUrl" => Url,
            "get_CurrentSession" => Online || KeepDisconnectedIdentity ? login : null,
            "get_CurrentUser" => Online || KeepDisconnectedIdentity
                ? new UserDto(AccountId, "controller", HostPlatformKind.Windows, "fixture", default, null) : null,
            "get_CurrentServer" => null,
            "add_StateChanged" or "remove_StateChanged" => null,
            _ => throw new NotSupportedException(method?.Name),
        };
    }

    public class PermissionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "Clear" ? null : throw new NotSupportedException(method?.Name);
    }

    private sealed class Diagnostics : RelaxKonOS.AppSDK.IAppActivationDiagnostics
    {
        public void Record(string message) => Console.WriteLine(message);
    }
}
