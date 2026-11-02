using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Services;
using RelaxKonOS.ManagedRuntimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.Runtime;

internal static class RoappCatalogChecks
{
    public static void Run(ShellSettings settings)
    {
        var task = RunAsync(settings);
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new Exception("Local package test timed out.");
        task.GetAwaiter().GetResult();
    }
    private static async Task RunAsync(ShellSettings settings)
    {
        var parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp"));
        var root = Path.Combine(parent, "roapp-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "manifests"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        using var services = new ServiceCollection().BuildServiceProvider();
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        var applications = new ApplicationManager(windows, services);
        var permissions = new Permissions();
        var packages = new DeveloperPackageManager(applications, null!, windows, permissions, new Diagnostics(),
            new VirtualSystemDrive(Path.Combine(root, "drive")));
        using var catalogSources = new ComponentCatalogSourceStore(Path.Combine(root, "catalog-trust"), "test-desktop", new DirectoryTransport(root));
        var installer = new AppPackageInstallerService(packages, null!, catalogSources);
        var provider = new RoappComponentInstallerProvider(installer);
        var source = new LocalCatalogSource(root, "test-local-source");
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        const string id = "example.catalog-app";
        var packagePath = Path.Combine(root, "packages", "example.roapp");
        async Task PublishAsync(string version)
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                await using (var entry = archive.CreateEntry("manifest.json").Open())
                    await JsonSerializer.SerializeAsync(entry, new DeveloperPackageManifest(id, "Catalog fixture", version,
                        "lib/fixture.dll", "Fixture.App", RequestedPermissions: [], PermissionModelVersion: 2), json);
                archive.CreateEntryFromFile(typeof(RoappCatalogChecks).Assembly.Location, "lib/fixture.dll");
            }
            var digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(packagePath)));
            var target = new ComponentInstallTarget(RoappComponentInstallerProvider.Desktop.Os,
                [RoappComponentInstallerProvider.Desktop.Architecture], new("roapp", true), "DesktopApp", false,
                new("catalog"), new("catalog-artifact", Format: "roapp", Reference: "../packages/example.roapp", Sha256: digest, Version: version));
            var manifest = new ComponentManifest(1, id, id, "system", "Fixture", "fixture.description", "Application", id,
                [], [], [], [id], [target]);
            await File.WriteAllTextAsync(Path.Combine(root, "manifests", "example.json"), JsonSerializer.Serialize(manifest, json));
            await File.WriteAllTextAsync(Path.Combine(root, "catalog.json"), JsonSerializer.Serialize(new ComponentCatalogIndex(1,
                [new(id, "./manifests/example.json")], []), json));
        }
        try
        {
            await PublishAsync("1.0.0");
            var plan = await provider.PlanAsync(source, id);
            using (var review = new AppInstallerViewModel(installer) { AllowPackageSelection = false })
            {
                review.QueueCatalogPlan(plan);
                Check(packages.FindInstalled(id) is null && review.HasPackage && review.CurrentPackage!.ExpectedSha256 is not null,
                    "catalog planning opens review without installing or granting permissions");
                await File.AppendAllTextAsync(plan.Package.LocalPath, "changed-after-review");
                await RejectAsync(() => installer.InstallAsync(plan.Package), "staged bytes changed after plan");
            }
            plan = await provider.PlanAsync(source, id);
            // Once staged, later catalog/source changes cannot substitute the reviewed bytes.
            await File.WriteAllTextAsync(packagePath, "source replaced");
            File.Delete(Path.Combine(root, "catalog.json"));
            var installed = await installer.InstallAsync(plan.Package);
            Check(installed.Version == "1.0.0" && packages.FindInstalled(id)?.Version == "1.0.0"
                && !applications.IsBuiltIn(new(id)), "existing installer installs exact frozen package after catalog removal without BuiltIn trust");
            await PublishAsync("1.0.1");
            var update = await provider.PlanAsync(source, id);
            var stale = await provider.PlanAsync(source, id);
            Check(update.Package.InstalledVersion == "1.0.0", "update plan freezes installed version");
            await installer.InstallAsync(update.Package);
            await RejectAsync(() => installer.InstallAsync(stale.Package), "installed version changed after planning");
            installer.Discard(stale.Package);
            Check(packages.FindInstalled(id)?.Version == "1.0.1" && permissions.Clears == 2,
                "domain update remains authoritative and resets permission decisions");
            await packages.UninstallAsync(id);
            Check(packages.FindInstalled(id) is null, "domain uninstall remains independent of catalog");
            Console.WriteLine("Local .roapp catalog plan/review/install/update checks passed.");

            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var sources = new CatalogSourcesViewModel(catalogSources)
            {
                SourceId = "desktop-test-source", PublisherId = "example", DisplayName = "Reviewed publisher",
                BaseUri = "https://desktop.example/catalog/", KeyId = "publisher-key",
                PublicKeySpki = Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo()), MaximumValiditySeconds = "604800"
            };
            sources.ReviewCommand.Execute(null);
            Check(sources.IsReviewing && sources.CanConfirm && catalogSources.Revision == 0, "review alone cannot trust a source: " + sources.Notice + " / " + sources.IsReviewing + " / " + sources.CanConfirm + " / " + catalogSources.Revision);
            sources.AllowApt = true;
            Check(!sources.IsReviewing && !sources.CanConfirm, "changing provider scope invalidates the displayed approval");
            sources.AllowApt = false; sources.ReviewCommand.Execute(null); sources.ConfirmCommand.Execute(null);
            Check(catalogSources.Sources().Single().Policy.InstallerProviders.SequenceEqual(["roapp"]), "only the explicitly reviewed provider allow-list is approved");
            var now = DateTimeOffset.UtcNow;
            var envelope = await ComponentCatalogPublisher.BuildAsync(root, "desktop-test-source", "example", 1, "publisher-key", signingKey,
                now.AddMinutes(-1), now.AddDays(7).AddMinutes(-1), [], []);
            await File.WriteAllBytesAsync(Path.Combine(root, "catalog.signed.json"), envelope);
            await sources.RefreshCommand.ExecuteAsync(new CatalogSourceRow(catalogSources.Sources().Single()));
            Check(catalogSources.Sources().Single().Status == "ready" && packages.FindInstalled(id) is null, "signed refresh discovers but never installs a package");
            ComponentCatalog.UseSources(catalogSources);
            Check(ComponentCatalog.Find(id)?.IsDesktop == true && ComponentCatalog.RemoteSourceId(id) == "desktop-test-source"
                && ComponentCatalog.LaunchGate(id) is null, "remote data adds a desktop component without rewriting built-in launch gates");
            var remotePlan = await provider.PlanAsync(ComponentCatalog.SourceFor(id), id);
            Check(remotePlan.Package.CatalogAuthorization is not null, "remote plan freezes durable source authorization");
            await installer.InstallAsync(remotePlan.Package);
            Check(packages.FindInstalled(id)?.Version == "1.0.1" && !applications.IsBuiltIn(new(id)), "remote signed package uses the real domain installer without BuiltIn promotion");
            var revokedPlan = await provider.PlanAsync(ComponentCatalog.SourceFor(id), id);
            Check(revokedPlan.Package.IsRepair && !revokedPlan.Package.IsUpdate, "same-version reinstall is a repair, never an upgrade");
            sources.EditCommand.Execute(new CatalogSourceRow(catalogSources.Sources().Single()));
            RenderSources(settings, sources);
            sources.CancelReviewCommand.Execute(null);
            catalogSources.SetEnabled("desktop-test-source", false, catalogSources.Revision);
            await RejectAsync(() => installer.InstallAsync(revokedPlan.Package), "source disabled after review");
            installer.Discard(revokedPlan.Package);
            Check(packages.FindInstalled(id)?.Version == "1.0.1", "source disable preserves installed facts");
            await packages.UninstallAsync(id);
            Console.WriteLine("Remote .roapp source review/discovery/installation/disable checks passed.");
        }
        finally
        {
            ComponentCatalog.UseSources(null);
            catalogSources.Dispose();
            if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unsafe test cleanup");
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or RuntimePolicyException) { return; }
        throw new Exception("Accepted " + message);
    }
    private static void RenderSources(ShellSettings settings, CatalogSourcesViewModel sources)
    {
        var language = settings.Language; var appearance = settings.Appearance;
        var output = Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "component-sources"); Directory.CreateDirectory(output);
        try
        {
            foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
            foreach (var dark in new[] { false, true })
            {
                settings.Language = culture;
                settings.Appearance = appearance with { Mode = dark ? RelaxKonOS.Protocol.Desktop.ThemeKind.Dark : RelaxKonOS.Protocol.Desktop.ThemeKind.Light };
                var view = new CatalogSourcesView { DataContext = sources };
                var window = new Window { Width = 900, Height = 950, Content = new ScrollViewer { Content = view, Padding = new Thickness(20) } };
                window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Check(view.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible
                    && Equals(button.Command, sources.ConfirmCommand) && button.IsEnabled), "explicit source approval is visible and enabled only after review");
                using var bitmap = new RenderTargetBitmap(new PixelSize(900, 950)); bitmap.Render(window);
                bitmap.Save(Path.Combine(output, culture + (dark ? "-dark" : "-light") + ".png"));
                window.Close(); Dispatcher.UIThread.RunJobs();
            }
        }
        finally { settings.Language = language; settings.Appearance = appearance; }
    }
    private sealed class DirectoryTransport(string root) : IComponentCatalogTransport
    {
        public async Task CopyAsync(ComponentCatalogSourcePolicy policy, string path, long maximumBytes, Stream destination, CancellationToken ct)
        {
            await using var input = await new LocalCatalogSource(root, policy.SourceId).OpenAsync(path, "catalog.json", ct);
            if (input.Length > maximumBytes) throw new IOException("Bounded fixture transport");
            await input.CopyToAsync(destination, ct);
        }
    }
    private sealed class Diagnostics : IAppActivationDiagnostics { public void Record(string message) { } }
    private sealed class Permissions : IAppPermissionManager
    {
        public int Clears;
        public AppPermissionStatus GetStatus(AppId id, string permission) => default;
        public bool IsGranted(AppId id, string permission) => false;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status) { }
        public void SetGranted(AppId id, string permission, bool granted) { }
        public void Clear(AppId id) => Clears++;
    }
}
