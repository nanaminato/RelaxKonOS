using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
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
    public static void Run()
    {
        var task = RunAsync();
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new Exception("Local package test timed out.");
        task.GetAwaiter().GetResult();
    }
    private static async Task RunAsync()
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
        var installer = new AppPackageInstallerService(packages, null!);
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
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unsafe test cleanup");
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Accepted " + message);
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
