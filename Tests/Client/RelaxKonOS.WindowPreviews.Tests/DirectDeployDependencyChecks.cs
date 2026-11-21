using System.Formats.Tar;
using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;

internal static class DirectDeployDependencyChecks
{
    public static void Run(string archive)
    {
        if (!DeveloperProfile.IsDebug) throw new InvalidOperationException("Build this check in Debug.");
        var test = RunAsync(Path.GetFullPath(archive));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!test.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        if (!test.IsCompleted) throw new TimeoutException("Direct Deploy dependency check timed out.");
        test.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string path)
    {
        var root = Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "direct-" + Guid.NewGuid().ToString("N"));
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        windows.Attach(new Canvas { Width = 1100, Height = 800 });
        windows.SetHostBounds(new RelaxKonOS.Core.Primitives.Rect(0, 0, 1100, 800));
        var guard = new Guard();
        using var services = new ServiceCollection().AddSingleton<IApplicationLaunchGuard>(guard).BuildServiceProvider();
        var apps = new ApplicationManager(windows, services);
        var packages = new DeveloperPackageManager(apps, null!, windows, new RoappCatalogChecks.Permissions(),
            new RoappCatalogChecks.Diagnostics(), new VirtualSystemDrive(root), root);
        guard.Packages = packages;
        var id = new AppId("relaxkonos.docker");
        using var payload = new MemoryStream();
        using (var archive = ZipFile.OpenRead(path))
        using (var tar = new TarWriter(payload, leaveOpen: true))
        {
            foreach (var entry in archive.Entries.Where(entry => entry.Name.Length > 0))
            {
                using var data = new MemoryStream();
                using (var source = entry.Open()) await source.CopyToAsync(data);
                data.Position = 0;
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entry.FullName) { DataStream = data });
            }
        }

        try
        {
            payload.Position = 0;
            var blocked = await packages.DeployAsync(payload, launch: true, default);
            if (blocked.Code != "launch_blocked" || !guard.Checked || apps.IsRegistered(id))
                throw new InvalidOperationException($"Candidate dependencies did not block Direct Deploy launch ({blocked.Code}).");

            payload.Position = 0;
            var staged = await packages.DeployAsync(payload, launch: false, default);
            if (staged.Code != "ok" || !apps.IsRegistered(id) || !guard.HasRequiredModule(id))
                throw new InvalidOperationException($"Direct Deploy did not register the dependency ({staged.Code}).");
        }
        finally
        {
            await packages.UninstallAsync(id.Value);
        }
        Console.WriteLine("Direct Deploy preserves required dependencies and checks them before candidate launch.");
    }

    private sealed class Guard : IApplicationLaunchGuard
    {
        public DeveloperPackageManager Packages { get; set; } = null!;
        public bool Checked { get; private set; }

        public bool HasRequiredModule(AppId id) => Packages.RequiredDependencies(id) is
            [{ Package: { Kind: DeploymentPackageKind.ServerModule, Id: "relaxkonos.docker" }, Capability: "server.docker" }];

        public bool RequiresCheck(ApplicationManifest manifest) => HasRequiredModule(manifest.Id);

        public Task<bool> CheckAsync(RelaxKonOS.AppSDK.AppContext context, ManagedWindow? existingWindow)
        {
            Checked = HasRequiredModule(context.AppId);
            return Task.FromResult(false);
        }
    }
}
