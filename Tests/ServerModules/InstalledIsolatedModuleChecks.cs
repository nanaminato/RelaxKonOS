using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Server.Residency;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Startup;

internal static class InstalledIsolatedModuleChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "installed-isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "HostFixture.dll"));
            var entry = "server/" + RelaxKonOS.ModulePackaging.ModulePackagePlatform.CurrentRid + "/HostFixture.dll";
            var manifest = new ModulePackageManifest(3, "server-module", "fixture-host", "fixture-host-release", "1.0.0",
                "fixture", "third-party", RelaxKonOS.ModulePackaging.ModulePackagePlatform.CurrentRid, "2.0.0", "3.0.0", entry,
                "HostFixture.FixtureHostEntry", "fixture-build", new string('a', 40), [], [],
                [new(entry, bytes.Length, Hash(bytes))], "module-host", [ModuleCapabilityFixtures.Service("fixture.service", "restart")],
                new("Fixture", "Isolation test", "", null, "MIT"));
            var metadata = JsonSerializer.SerializeToUtf8Bytes(manifest, ModulePackageJson.Options);
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var file = zip.CreateEntry("manifest.json").Open()) file.Write(metadata);
                using (var file = zip.CreateEntry(entry).Open()) file.Write(bytes);
            }
            var record = await InstalledModuleInstaller.InstallVerifiedAsync(root, "fixture", manifest, Hash(metadata),
                Hash(archive.ToArray()), archive, DateTimeOffset.UtcNow);
            check(record.Runtime == "module-host" && InstalledModuleStore.Read(root).Single().Runtime == "module-host",
                "v2 installation durably preserves the explicit runtime");
            var module = InstalledModuleCatalog.Load(root).Single();
            check(module is InstalledIsolatedModule, "v2 third-party package selects metadata-only Core adapter");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
            module.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            check(!Directory.Exists(Path.Combine(root, "data", "module-data")),
                "Core registration never executes the isolated module entry or storage initialization");
            check(ModuleTrustStore.Read(root, record) is null, "installing bytes alone never silently grants third-party code trust");
            ModuleTrustStore.Set(root, record, "test-administrator", trusted: true);
            check(ModuleTrustStore.Read(root, record)?.Trusted == true
                && ModuleTrustStore.Read(root, record with { ArtifactSha256 = new string('c', 64) }) is null,
                "code trust binds to the exact package and cannot survive changed bytes");
            var nextManifest = manifest with { Version = "2.0.0", ReleaseId = "fixture-host-next" };
            var next = await StageAsync(root, nextManifest, bytes);
            ModuleTrustStore.Set(root, next, "test-administrator", trusted: true, preserve: record);
            check(InstalledModuleStore.Read(root).Single().Version == record.Version
                && InstalledModuleStore.ReadVersions(root, record.ModuleId).Count == 2
                && ModuleTrustStore.Read(root, record)?.Trusted == true && ModuleTrustStore.Read(root, next)?.Trusted == true,
                "staging a trusted candidate leaves the previous version selected and launchable");

            var operation = ModulePackageOperationStore.Record(root, "update", next, record.Version,
                DateTimeOffset.UtcNow, online: true);
            var interrupted = ModulePackageOperationStore.VerifyStartup(root, InstalledModuleStore.Read(root),
                "next-core", DateTimeOffset.UtcNow).Single();
            check(interrupted.OperationId == operation.OperationId && interrupted.State == ModulePackageOperationState.Failed
                && interrupted.ProblemCode == "module-host-update-interrupted",
                "a Core interruption before candidate activation keeps the previous version and records the interrupted update");
            var bad = await StageAsync(root, nextManifest with { Version = "3.0.0", ReleaseId = "fixture-host-unhealthy",
                EntryType = "HostFixture.FixtureUnhealthyHostEntry" }, bytes);
            ModuleTrustStore.Set(root, bad, "test-administrator", trusted: true, preserve: record);
            var modules = ServerModules.CreateManaged(new HashSet<string> { record.ModuleId }, [module]);
            using (var management = new ModuleManagementStore(builder.Configuration, root, [module]))
            {
                modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
                await using var app = builder.Build();
                using var supervisor = new ModuleHostSupervisor(builder.Environment, builder.Configuration,
                    NullLogger<ModuleHostSupervisor>.Instance,
                    app.Services.GetRequiredService<IResidentModuleWorkLeaseSource>(), modules,
                    (packageRoot, id, dataPath, installed) => new(packageRoot, id, dataPath, installed: installed));
                await supervisor.StartAsync(CancellationToken.None);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (supervisor.Snapshot().SingleOrDefault()?.Status != "running")
                        await Task.Delay(25, timeout.Token);
                    var failed = await supervisor.ReplaceAsync(record, bad,
                        () => InstalledModuleInstaller.Activate(root, bad), timeout.Token);
                    check(failed == "module-host-update-rolled-back" && supervisor.Snapshot().Single().Version == record.Version
                        && supervisor.Snapshot().Single().Status == "running" && InstalledModuleStore.Read(root).Single().Version == record.Version,
                        "a real unhealthy candidate exits and the previous package is restarted without committing the candidate");
                    ModuleTrustStore.Set(root, next, "test-administrator", trusted: true, preserve: record);
                    var updated = await supervisor.ReplaceAsync(record, next,
                        () => InstalledModuleInstaller.Activate(root, next), timeout.Token);
                    check(updated is null && supervisor.Snapshot().Single().Version == next.Version
                        && supervisor.Snapshot().Single().Status == "running" && InstalledModuleStore.Read(root).Single().Version == next.Version,
                        "a healthy real candidate becomes the active package without restarting Core");
                }
                finally { await supervisor.StopAsync(CancellationToken.None); }
            }
            ModuleTrustStore.Set(root, record, "test-administrator", trusted: false);
            check(ModuleTrustStore.Read(root, record)?.Trusted == false, "code trust revocation is durable and immediately observable");
            var path = Path.Combine(InstalledModuleStore.ResolvePackageRoot(root, record.PackagePath), "manifest.json");
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(manifest with { Runtime = "in-process", Purpose = "official", Privileges = [] }, ModulePackageJson.Options));
            try
            {
                InstalledModuleStore.VerifyPackage(root, record);
                check(false, "runtime tampering rejected");
            }
            catch (ModulePackageException error)
            {
                check(error.ProblemCode == "module-package-installed-integrity", "installed manifest digest prevents runtime downgrade tampering");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static async Task<InstalledModuleRecord> StageAsync(string root, ModulePackageManifest manifest, byte[] bytes)
    {
        var metadata = JsonSerializer.SerializeToUtf8Bytes(manifest, ModulePackageJson.Options);
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var file = zip.CreateEntry("manifest.json").Open()) file.Write(metadata);
            using (var file = zip.CreateEntry(manifest.EntryAssembly).Open()) file.Write(bytes);
        }
        return await InstalledModuleInstaller.InstallVerifiedAsync(root, "fixture", manifest, Hash(metadata),
            Hash(archive.ToArray()), archive, DateTimeOffset.UtcNow, activate: false);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
