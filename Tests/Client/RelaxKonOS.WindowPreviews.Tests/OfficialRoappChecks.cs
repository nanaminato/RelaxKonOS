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
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes.Catalog;
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
        var isTaskManager = applicationId == "relaxkonos.taskmanager";
        var isRegistry = applicationId == "relaxkonos.registry";
        var isBrowser = applicationId == "relaxkonos.browser";
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
        try
        {
            var (packages, applications, windows) = CreateRuntime();
            var installer = new AppPackageInstallerService(packages, null!, catalog);
            File.Copy(artifact, path);
            File.Copy(artifact + ".sha256", path + ".sha256");
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
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage, AppPermissions.ServerComponentsRead]
                    && candidate.Manifest.ServerRequirements?.RequiredCapabilities is ["server.docker"],
                    "Docker review exposes the required server capability and all three permissions.");
            if (isRegistry)
                Check(candidate.Manifest.RequestedPermissions is [AppPermissions.ServerRegistryRead, AppPermissions.ServerRegistryWrite],
                    "Registry review exposes separate read and write grants.");
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
            await provisioner.RunAsync(["--installation-profile=developer"]);
            if (isDocker)
                Check(packages.FindInstalled(applicationId) is null, "Docker stays optional even in the Developer profile.");
            var first = isDocker
                ? await installer.InstallAsync(await installer.InspectLocalAsync(path))
                : packages.FindInstalled(applicationId) ?? throw new InvalidOperationException("The bundled default did not install.");
            if (!isDocker)
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
            if (isDocker || isTaskManager || isRegistry || isBrowser)
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
                    _ => "RelaxKonOS.App.Docker.Views.DockerManagerWorkspace",
                };
                Check(workspace.Info.OwnerAppId == id && workspace.View.Content?.GetType().FullName == workspaceType,
                    "The installed entry point must create its own workspace.");
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
                if (isBrowser)
                {
                    settings.Language = "en-US";
                    Dispatcher.UIThread.RunJobs();
                    Check(workspace.Title == "リモート ブラウザー", "A closed Browser window should release its language subscription.");
                }
            }

            await PublishCandidateAsync(manifest => manifest["version"] = "1.1.0");
            var update = await installer.InspectLocalAsync(path);
            Check(update.IsUpdate, "The second release should be offered as an update.");
            await File.AppendAllTextAsync(path, "modified after review");
            await RejectAsync(() => installer.InstallAsync(update), "Changed package after review");
            Check(packages.FindInstalled(applicationId) == first, "The changed bytes should not replace the current version.");

            await PublishCandidateAsync(manifest =>
            {
                manifest["version"] = "1.1.0";
                manifest["entryType"] = "Fixture.DoesNotExist";
            });
            await RejectAsync(async () => await installer.InstallAsync(await installer.InspectLocalAsync(path)), "Invalid entry type");
            Check(packages.FindInstalled(applicationId) == first && applications.IsRegistered(id), "A failed candidate must keep the registered version.");

            await PublishCandidateAsync(manifest =>
            {
                manifest["version"] = "1.1.0";
                manifest["clientPlatforms"] = new JsonArray("unsupported-test-platform");
            });
            await RejectAsync(async () => await installer.InstallAsync(await installer.InspectLocalAsync(path)), "Incompatible client");

            await PublishCandidateAsync(manifest => manifest["version"] = "1.1.0",
                archive => archive.CreateEntry("../outside.dll"));
            await RejectAsync(async () => await installer.InstallAsync(await installer.InspectLocalAsync(path)), "Unsafe archive path");

            await PublishCandidateAsync(manifest => manifest["version"] = "1.1.0");
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
            var upgraded = await installer.InstallAsync(await installer.InspectLocalAsync(path));
            if (guarded is not null)
                Check(!windows.Windows.Contains(guarded), "The accepted update should retire the old window.");
            Check(upgraded.Version == "1.1.0" && upgraded.InstallationPath != first.InstallationPath && Directory.Exists(first.InstallationPath),
                "The upgrade should leave the previous package version intact.");

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
            var memory = new UsageMemoryStore(Path.Combine(root, "usage-memory.json"));
            var context = new ExternalAppContextFactory(permissions, null!, language, settings, null!, auth, null!,
                windows, null!, new ExplorerClient(http, auth, memory), new SshDesktopSession(null!, settings),
                null!, null!, null!, null!, null!, memory, null!, null!, null!, null!);
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

        Task WriteProfileAsync(string policy)
        {
            var entry = new[] { new { appId = applicationId, minimumVersion = isDocker ? "0.2.0" : "1.0.0", installPolicy = policy } };
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
