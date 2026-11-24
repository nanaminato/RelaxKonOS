using System.IO.Compression;
using RelaxKonOS.Client.Localization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Apps.AppInstaller.Views;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.Runtime;
using WindowManager = RelaxKonOS.WindowManager.WindowManager;

internal static class OfficialStoreChecks
{
    private const string AppIdText = "relaxkonos.notepad";

    public static void Run(string artifact, string? catalog = null)
    {
        if (DeveloperProfile.IsDebug) throw new InvalidOperationException("Use Release for the official store check.");
        var task = RunAsync(Path.GetFullPath(artifact), catalog is null ? null : Path.GetFullPath(catalog));
        var deadline = DateTime.UtcNow.AddSeconds(80);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new InvalidOperationException("Official store check timed out.");
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string artifact, string? catalog)
    {
        var root = Directory.CreateTempSubdirectory("relaxkonos-official-store-");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var host = builder.Build();
        var packagesOnline = new Dictionary<string, byte[]>();
        var releases = Array.Empty<object>();
        var redirect = false;
        (TaskCompletionSource Started, TaskCompletionSource Aborted)? pausedDownload = null;
        host.MapGet("/stable/catalog.json", () => catalog is null
            ? Results.Json(new { schemaVersion = 1, releases })
            : Results.File(catalog, "application/json"));
        host.MapGet("/stable/{app}/{version}.roapp", async (string app, string version, HttpContext context) =>
        {
            if (pausedDownload is { } paused)
            {
                paused.Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    paused.Aborted.TrySetResult();
                    return Results.Empty;
                }
            }
            return redirect ? Results.Redirect("/stable/catalog.json") : app == AppIdText && packagesOnline.TryGetValue(version, out var bytes)
                ? Results.Bytes(bytes, "application/octet-stream") : Results.NotFound();
        });
        await host.StartAsync();

        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (message, served, _, _) =>
                message.RequestUri?.IsLoopback == true && served?.Thumbprint == certificate.Thumbprint,
        });
        using var services = new ServiceCollection().BuildServiceProvider();
        using var trust = new ComponentCatalogSourceStore(Path.Combine(root.FullName, "source-trust"), "official-store-test");
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, "OfficialApps")).FullName;
        var sourceConfig = Path.Combine(source, OfficialDesktopPackages.SourceFile);
        await File.WriteAllTextAsync(sourceConfig, System.Text.Json.JsonSerializer.Serialize(new
        {
            catalogUri = host.Urls.Single() + "/stable/catalog.json",
        }));
        var drive = new VirtualSystemDrive(Path.Combine(root.FullName, "drive"));
        var windows = new WindowManager();
        var applications = new ApplicationManager(windows, services);
        var packages = new DeveloperPackageManager(applications, null!, windows, new RoappCatalogChecks.Permissions(),
            new RoappCatalogChecks.Diagnostics(), drive, source, http);
        var installer = new AppPackageInstallerService(packages, null!, trust);
        var id = new AppId(AppIdText);
        try
        {
            var v1 = await File.ReadAllBytesAsync(artifact);
            var v2 = ReadPackage(artifact, "1.1.0");
            var broken = ReadPackage(artifact, "1.2.0", "Fixture.MissingEntry");
            var interrupted = ReadPackage(artifact, "1.3.0");
            packagesOnline["1.0.0"] = v1;
            packagesOnline["1.1.0"] = v2;
            packagesOnline["1.2.0"] = broken;
            packagesOnline["1.3.0"] = interrupted;
            releases = [Release("1.0.0", v1)];
            var releaseCount = catalog is null ? 1 : OfficialDesktopPackages.Ids.Count;
            Check((await packages.OfficialSource.BrowseOnlineAsync(default)).Count == releaseCount,
                "The preconfigured HTTPS source publishes the expected releases.");
            using var model = new AppInstallerViewModel(installer) { ConfirmUninstallAsync = _ => Task.FromResult(true) };
            var view = new AppInstallerView { DataContext = model };
            var window = new Window { Width = 620, Height = 580, Content = view };
            window.Show();
            try
            {
                await model.RefreshOfficialCommand.ExecuteAsync(null);
                model.SelectedOfficialApp = model.OfficialApps.Single(item => item.Id == AppIdText);
                Check(model.OfficialApps.Count == releaseCount && model.SelectedOfficialApp?.AvailableVersion == "1.0.0",
                    $"Browse the official HTTPS catalog (rows {model.OfficialApps.Count}, selection {model.SelectedOfficialApp?.AvailableVersion}).");
                await model.DownloadOfficialCommand.ExecuteAsync(null);
                var firstReview = model.CurrentPackage;
                Check(firstReview?.Manifest.Id == AppIdText && firstReview.DeleteWhenFinished && !applications.IsRegistered(id),
                    "Download stages an official package and waits for review.");
                Check((await File.ReadAllBytesAsync(firstReview!.LocalPath)).AsSpan().SequenceEqual(v1),
                    "The first review stages the unmodified official release archive.");
                await model.InstallCommand.ExecuteAsync(null);
                var first = packages.FindInstalled(AppIdText);
                Check(first is { Version: "1.0.0" }, $"Install should activate version 1.0.0. {model.Status}");
                Check(applications.SupportsFile(id, "/files/README"), "Install registers the Notepad file association.");
                Check(!File.Exists(firstReview.LocalPath), "Install removes the downloaded artifact.");

                var document = drive.ResolveRootChild($"Users/{drive.LocalProfileId}/Documents/repair-check.txt");
                const string documentContents = "Keep this document when repairing Notepad.";
                drive.EnsureCreated();
                await File.WriteAllTextAsync(document, documentContents);
                var installedIcon = Path.Combine(first!.InstallationPath, "Assets", "notepad.png");
                var originalIcon = await File.ReadAllBytesAsync(installedIcon);
                File.Delete(installedIcon);

                Check(model.DownloadOfficialCommand.CanExecute(null)
                    && model.OfficialActionLabel == LocalizedText.Get("app_installer.action.repair"),
                    "The trusted installed version is available for repair.");
                await model.DownloadOfficialCommand.ExecuteAsync(null);
                Check(model.CurrentPackage?.IsRepair == true, "Repair requires a same-version package review.");
                await model.InstallCommand.ExecuteAsync(null);
                var repaired = packages.FindInstalled(AppIdText);
                Check(repaired is { Version: "1.0.0" } && repaired.InstallationPath != first!.InstallationPath
                    && Directory.Exists(first.InstallationPath) && applications.IsRegistered(id),
                    "Repair verifies and promotes a fresh version directory while preserving the version and registration.");
                Check((await File.ReadAllBytesAsync(Path.Combine(repaired!.InstallationPath, "Assets", "notepad.png")))
                        .SequenceEqual(originalIcon) && await File.ReadAllTextAsync(document) == documentContents,
                    "Repair restores the missing installed asset and preserves the user's document.");

                catalog = null;
                releases = [Release("1.0.0", v1), Release("1.1.0", v2)];
                await model.RefreshOfficialCommand.ExecuteAsync(null);
                Check(model.SelectedOfficialApp?.AvailableVersion == "1.1.0", "Browse selects the newest version.");
                var release = model.SelectedOfficialApp!.Release!;
                redirect = true;
                await RejectAsync(() => installer.StageOfficialAsync(release, default), "Redirect");
                redirect = false;
                var corrupted = (byte[])v2.Clone();
                corrupted[^1] ^= 1;
                packagesOnline["1.1.0"] = corrupted;
                await RejectAsync(() => installer.StageOfficialAsync(release, default), "Corrupt download");
                await model.DownloadOfficialCommand.ExecuteAsync(null);
                Check(model.CurrentPackage is null && packages.FindInstalled(AppIdText) == repaired
                    && model.Status.ToString() == LocalizedText.Get("app_installer.store.download_failed"),
                    "The installer reports the corrupt download without offering review or replacing the installed app.");
                packagesOnline["1.1.0"] = v2;
                var downloadSignals = (Started: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    Aborted: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                pausedDownload = downloadSignals;
                var pendingDownload = model.DownloadOfficialCommand.ExecuteAsync(null);
                try
                {
                    await downloadSignals.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    model.DownloadOfficialCommand.Cancel();
                    await pendingDownload;
                    await downloadSignals.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Check(model.CurrentPackage is null && packages.FindInstalled(AppIdText) == repaired
                        && model.Status.ToString() == LocalizedText.Get("app_installer.store.download_cancelled"),
                        "Cancelling a live HTTPS download leaves the installed app available and opens no review.");
                }
                finally
                {
                    model.DownloadOfficialCommand.Cancel();
                    pausedDownload = null;
                }
                var oldSource = await File.ReadAllTextAsync(sourceConfig);
                var oldReview = await installer.StageOfficialAsync(release, default);
                try
                {
                    await File.WriteAllTextAsync(sourceConfig, oldSource.Replace("/stable/", "/changed/", StringComparison.Ordinal));
                    await RejectAsync(() => installer.InstallAsync(oldReview), "Source changed after review");
                }
                finally
                {
                    await File.WriteAllTextAsync(sourceConfig, oldSource);
                    installer.Discard(oldReview);
                }
                await model.DownloadOfficialCommand.ExecuteAsync(null);
                Check(model.CurrentPackage?.IsUpdate == true, "The review compares the installed and available versions.");
                await model.InstallCommand.ExecuteAsync(null);
                var active = packages.FindInstalled(AppIdText);
                Check(active is { Version: "1.1.0" } && Directory.Exists(first!.InstallationPath),
                    "The verified update leaves the previous version on disk.");

                releases = [Release("1.2.0", broken)];
                await model.RefreshOfficialCommand.ExecuteAsync(null);
                var invalid = await installer.StageOfficialAsync(model.SelectedOfficialApp!.Release!, default);
                try { await RejectAsync(() => installer.InstallAsync(invalid), "Broken entry assembly"); }
                finally { installer.Discard(invalid); }
                Check(packages.FindInstalled(AppIdText) == active && applications.SupportsFile(id, "/files/README"),
                    "A failed update keeps the current version registered.");
                releases = [Release("1.3.0", interrupted)];
                await model.RefreshOfficialCommand.ExecuteAsync(null);
                var candidate = await installer.StageOfficialAsync(model.SelectedOfficialApp!.Release!, default);
                var oldRegistration = applications.Get(id);
                const string interruption = "Test interruption after switching the registry.";
                var swapped = false;
                void FailRegistration(object? sender, EventArgs args)
                {
                    if (applications.GetManifest(id)?.Version != "1.3.0") return;
                    swapped = true;
                    throw new IOException(interruption);
                }
                applications.RegistryChanged += FailRegistration;
                try
                {
                    await installer.InstallAsync(candidate);
                    throw new InvalidOperationException("Interrupted registration was accepted.");
                }
                catch (InvalidOperationException error) when (error.Message == interruption) { }
                finally
                {
                    applications.RegistryChanged -= FailRegistration;
                    installer.Discard(candidate);
                }
                Check(swapped && ReferenceEquals(applications.Get(id), oldRegistration) && packages.FindInstalled(AppIdText) == active,
                    $"Rollback restores the previous ApplicationManager entry after it was replaced. " +
                    $"Swapped: {swapped}, registered: {applications.GetManifest(id)?.Version}, installed: {packages.FindInstalled(AppIdText)?.Version}.");
                var reopened = new ApplicationManager(windows, services);
                var restored = new DeveloperPackageManager(reopened, null!, windows, new RoappCatalogChecks.Permissions(),
                    new RoappCatalogChecks.Diagnostics(), drive, source, http);
                await restored.LoadInstalledAsync();
                Check(restored.FindInstalled(AppIdText) == active && reopened.SupportsFile(id, "/files/README"),
                    "The rolled-back version loads again from the VSD.");

                var bundled = Path.Combine(source, AppIdText + ".roapp");
                await File.WriteAllBytesAsync(bundled, v1);
                await File.WriteAllTextAsync(bundled + ".sha256", Digest(v1));
                releases = [Release("1.0.0", v1)];
                await model.RefreshOfficialCommand.ExecuteAsync(null);
                Check(model.SelectedOfficialApp?.Release is { Version: "1.0.0", CatalogUri: null },
                    "A version already bundled with the client needs no download.");
                await File.WriteAllTextAsync(sourceConfig, """{"catalogUri":"http://invalid.test/catalog.json"}""");
                await model.RefreshOfficialCommand.ExecuteAsync(null);
                Check(model.SelectedOfficialApp?.AvailableVersion == "1.0.0" && !model.DownloadOfficialCommand.CanExecute(null),
                    "Bundled releases stay browsable after a source failure, and no downgrade is offered.");
                await model.UninstallOfficialCommand.ExecuteAsync(null);
                Check(packages.FindInstalled(AppIdText) is null && !applications.IsRegistered(id),
                    "The store uninstalls via the existing application lifecycle.");
            }
            finally { window.Close(); }
            Console.WriteLine("PASS: official HTTPS browse, download, review, install, update, failure rollback, bundled fallback and uninstall.");
        }
        finally
        {
            await packages.UninstallAsync(AppIdText);
            // Windows may hold the package DLLs until this acceptance process exits.
            try { root.Delete(recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static object Release(string version, byte[] content) => new
    {
        appId = AppIdText, version, displayName = "Notepad", description = "Text editor",
        sha256 = Digest(content), bytes = content.LongLength,
    };

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] ReadPackage(string path, string version, string? entryType = null)
    {
        using var memory = new MemoryStream();
        using (var source = File.OpenRead(path)) source.CopyTo(memory);
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry("manifest.json")!;
            JsonObject manifest;
            using (var input = entry.Open()) manifest = JsonNode.Parse(input)!.AsObject();
            entry.Delete();
            manifest["version"] = version;
            if (entryType is not null) manifest["entryType"] = entryType;
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write(manifest.ToJsonString());
        }
        return memory.ToArray();
    }

    private static async Task RejectAsync(Func<Task> action, string scenario)
    {
        try { await action(); }
        catch (Exception error) when (AppPackageInstallerService.IsStoreFailure(error)) { return; }
        throw new InvalidOperationException(scenario + " was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
