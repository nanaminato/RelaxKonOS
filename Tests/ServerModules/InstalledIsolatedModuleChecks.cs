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
            var entry = "server/" + RuntimeInformation.RuntimeIdentifier + "/HostFixture.dll";
            var manifest = new ModulePackageManifest(2, "server-module", "fixture-host", "fixture-host-release", "1.0.0",
                "fixture", "third-party", RuntimeInformation.RuntimeIdentifier, "2.0.0", "3.0.0", entry,
                "HostFixture.FixtureHostEntry", "fixture-build", new string('a', 40), [], [],
                [new(entry, bytes.Length, Hash(bytes))], "module-host", [new("service-restart", "fixture.service")],
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
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
