using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Startup;
using ServerModulesCatalog = RelaxKonOS.Server.Startup.ServerModules;

internal static class InstalledModuleChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "installed-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ModulePackageChecks.WriteFixture(root);
            var serviceId = ModuleManagementStore.ReadRecoveryReport(root).ServiceId;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = root,
                EnvironmentName = Environments.Development,
            });
            builder.Configuration.AddJsonFile("appsettings.host.json", optional: false, reloadOnChange: false);
            var policy = builder.Configuration.GetSection("ModulePackages:Sources:official").Get<ModulePackageTrustPolicy>()
                ?? throw new Exception("installed module fixture has no trust policy");
            var context = ModulePackageHostConfiguration.Context(serviceId, builder.Configuration, builder.Environment);
            var catalogPath = Path.Combine(root, "catalog.json");
            var archivePath = Path.Combine(root, "candidate.rkmodule");
            var now = DateTimeOffset.UtcNow;
            using (var catalog = new ModulePackageCatalogStore(root, serviceId))
            {
                catalog.Accept(File.ReadAllBytes(catalogPath), policy, context, ModulePackageCatalogStore.InitialRevision,
                    "test-actor", "fixture-key", now);
            }

            var record = await InstalledModuleInstaller.InstallAsync(root, builder.Configuration, builder.Environment,
                "official", archivePath, "fixture-release", now);
            check(record.ModuleId == "example" && record.Version == "1.0.0" && record.SourceId == "official",
                "installed module records the verified release identity");
            check(File.Exists(Path.Combine(root, "data", "module-management", record.PackagePath, "manifest.json"))
                && File.Exists(Path.Combine(root, "data", "module-management", record.PackagePath, record.EntryAssembly)),
                "installed module writes immutable package files");
            check(ModulePackageCatalogStore.Read(root, serviceId).Sources.Count == 1,
                "installed module does not silently mutate catalog trust");

            var installed = InstalledModuleCatalog.Load(root);
            check(installed.Count == 1 && installed[0].Id == "example",
                "installed module metadata is discoverable without executing its assembly");
            var modules = ServerModulesCatalog.Create(new ConfigurationBuilder().Build(), installed);
            check(modules.KnownModuleIds.Contains("example") && modules.IsEnabled("example"),
                "installed module joins the known catalog and is enabled by the default set");
            var disabled = ServerModulesCatalog.CreateManaged(new HashSet<string>(StringComparer.Ordinal) { "relaxkonos.terminal" }, installed);
            check(disabled.KnownModuleIds.Contains("example") && !disabled.IsEnabled("example"),
                "managed enable set can leave an installed module disabled without losing it");

            var replay = await InstalledModuleInstaller.InstallAsync(root, builder.Configuration, builder.Environment,
                "official", archivePath, "fixture-release", now);
            check(replay.ModuleId == record.ModuleId && replay.ArtifactSha256 == record.ArtifactSha256,
                "installing the same verified release is idempotent");

            var output = new StringWriter();
            var original = Console.Out;
            int exit;
            try
            {
                Console.SetOut(output);
                exit = await ModulePackageMaintenanceCommand.RunAsync(["modules", "list-installed", "--content-root", root]);
            }
            finally { Console.SetOut(original); }
            using var document = JsonDocument.Parse(output.ToString());
            check(exit == 0 && document.RootElement.GetProperty("readOnly").GetBoolean()
                && document.RootElement.GetProperty("modules").GetArrayLength() == 1,
                "list-installed reports installation facts without claiming effective state");

            var rid = RuntimeInformation.RuntimeIdentifier;
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "FixtureModule.dll");
            var fixtureBytes = await File.ReadAllBytesAsync(fixturePath);
            var entryAssembly = "server/" + rid + "/FixtureModule.dll";
            var fixtureManifest = new ModulePackageManifest(1, "server-module", "fixture-module", "fixture-module-release",
                "1.0.0", "fixture", "official", rid, "1.0.0", "2.0.0", entryAssembly,
                "FixtureModule.FixtureInstalledModule", "fixture-build", new string('a', 40), [], [],
                [new(entryAssembly, fixtureBytes.Length, Hash(fixtureBytes))]);
            var fixtureManifestBytes = JsonSerializer.SerializeToUtf8Bytes(fixtureManifest, ModulePackageJson.Options);
            using var package = new MemoryStream();
            using (var zip = new ZipArchive(package, ZipArchiveMode.Create, true))
            {
                using (var stream = zip.CreateEntry("manifest.json").Open()) stream.Write(fixtureManifestBytes);
                using (var stream = zip.CreateEntry(entryAssembly).Open()) stream.Write(fixtureBytes);
            }
            var packageBytes = package.ToArray();
            var fixtureRecord = await InstalledModuleInstaller.InstallVerifiedAsync(root, "fixture", fixtureManifest,
                Hash(fixtureManifestBytes), Hash(packageBytes), new MemoryStream(packageBytes), now);
            check(fixtureRecord.ModuleId == "fixture-module", "verified installer accepts a real module assembly package");

            var dependencyConfiguration = new ConfigurationBuilder().Build();
            Variable("missing");
            Variable("self");
            InstalledModuleInstaller.ValidateDependencies(root, dependencyConfiguration, fixtureManifest with
            {
                Dependencies = [new ModulePackageDependency("relaxkonos.git", "1.0.0", "2.0.0")],
            });
            check(true, "dependency preflight accepts a known compiled module");
            void Variable(string kind)
            {
                var manifest = kind == "missing"
                    ? fixtureManifest with { Dependencies = [new ModulePackageDependency("missing-module", "1.0.0", "2.0.0")] }
                    : fixtureManifest with { Dependencies = [new ModulePackageDependency("fixture-module", "1.0.0", "2.0.0")] };
                try
                {
                    InstalledModuleInstaller.ValidateDependencies(root, dependencyConfiguration, manifest);
                    check(false, "dependency preflight rejects " + kind);
                }
                catch (ModulePackageException error)
                {
                    check(error.ProblemCode == (kind == "missing"
                        ? "module-package-dependency-missing" : "module-package-dependency-self"),
                        "dependency preflight rejects " + kind);
                }
            }

            var secondManifest = fixtureManifest with { Version = "1.0.1", ReleaseId = "fixture-module-release-2" };
            var secondManifestBytes = JsonSerializer.SerializeToUtf8Bytes(secondManifest, ModulePackageJson.Options);
            using var secondPackage = new MemoryStream();
            using (var zip = new ZipArchive(secondPackage, ZipArchiveMode.Create, true))
            {
                using (var stream = zip.CreateEntry("manifest.json").Open()) stream.Write(secondManifestBytes);
                using (var stream = zip.CreateEntry(entryAssembly).Open()) stream.Write(fixtureBytes);
            }
            var secondPackageBytes = secondPackage.ToArray();
            await InstalledModuleInstaller.InstallVerifiedAsync(root, "fixture", secondManifest,
                Hash(secondManifestBytes), Hash(secondPackageBytes), new MemoryStream(secondPackageBytes), now);
            check(InstalledModuleStore.Read(root).Single(module => module.ModuleId == "fixture-module").Version == "1.0.1",
                "installing a newer version switches the active installation fact but keeps the old directory");
            check(InstalledModuleStore.ReadVersions(root, "fixture-module").Select(module => module.Version).SequenceEqual(["1.0.0", "1.0.1"]),
                "installed version inventory reads both immutable versions without loading them");
            var versionsOutput = new StringWriter();
            var versionsOriginal = Console.Out;
            int versionsExit;
            try
            {
                Console.SetOut(versionsOutput);
                versionsExit = await ModulePackageMaintenanceCommand.RunAsync(["modules", "list-versions",
                    "--content-root", root, "--module-id", "fixture-module"]);
            }
            finally { Console.SetOut(versionsOriginal); }
            using var versionsDocument = JsonDocument.Parse(versionsOutput.ToString());
            check(versionsExit == 0 && versionsDocument.RootElement.GetProperty("readOnly").GetBoolean()
                && versionsDocument.RootElement.GetProperty("versions").GetArrayLength() == 2,
                "list-versions reports immutable installed versions without claiming the active one");
            var rolledBack = await InstalledModuleInstaller.RollbackAsync(root, "fixture-module", "1.0.0");
            check(rolledBack.Version == "1.0.0"
                && InstalledModuleStore.Read(root).Single(module => module.ModuleId == "fixture-module").Version == "1.0.0",
                "rollback switches the installation fact to a previously verified version");

            var installOperation = ModulePackageOperationStore.Record(root, "install", fixtureRecord, null, now);
            check(ModulePackageOperationStore.Read(root).Single().State == ModulePackageOperationState.AwaitingRestart,
                "install records an awaiting-restart package operation");
            var verifiedOperations = ModulePackageOperationStore.VerifyStartup(root, InstalledModuleStore.Read(root), "test-generation", now);
            check(verifiedOperations.Single(operation => operation.OperationId == installOperation.OperationId).State
                == ModulePackageOperationState.Succeeded, "startup verifies the exact installed package target");
            var newerRecord = InstalledModuleStore.ReadVersions(root, "fixture-module").Single(module => module.Version == "1.0.1");
            var mismatchOperation = ModulePackageOperationStore.Record(root, "install", newerRecord, null, now);
            var mismatched = ModulePackageOperationStore.VerifyStartup(root, InstalledModuleStore.Read(root), "test-generation", now);
            check(mismatched.Single(operation => operation.OperationId == mismatchOperation.OperationId).State
                == ModulePackageOperationState.Failed, "startup marks a different installed target as failed instead of success");
            var operationsOutput = new StringWriter();
            var operationsOriginal = Console.Out;
            int operationsExit;
            try
            {
                Console.SetOut(operationsOutput);
                operationsExit = await ModulePackageMaintenanceCommand.RunAsync(["modules", "list-package-operations",
                    "--content-root", root]);
            }
            finally { Console.SetOut(operationsOriginal); }
            using var operationsDocument = JsonDocument.Parse(operationsOutput.ToString());
            check(operationsExit == 0 && operationsDocument.RootElement.GetProperty("readOnly").GetBoolean()
                && operationsDocument.RootElement.GetProperty("operations").GetArrayLength() == 2,
                "list-package-operations reports durable install and rollback receipts");

            var currentModules = InstalledModuleStore.Read(root).ToList();
            currentModules[currentModules.FindIndex(module => module.ModuleId == "fixture-module")] = newerRecord;
            InstalledModuleStore.Write(root, currentModules);
            var failedUpdate = ModulePackageOperationStore.Record(root, "install", newerRecord, "1.0.0", now);
            ModulePackageStartupGuard.Begin(root, "startup-attempt-a");
            ModulePackageStartupGuard.Begin(root, "startup-attempt-b");
            var restored = InstalledModuleStore.Read(root).Single(module => module.ModuleId == "fixture-module");
            var recovered = ModulePackageOperationStore.Read(root).Single(operation => operation.OperationId == failedUpdate.OperationId);
            check(restored.Version == "1.0.0" && recovered.State == ModulePackageOperationState.Failed
                && recovered.ProblemCode == "module-package-operation-startup-rollback",
                "a surviving startup marker restores the previous version and records the failed update");
            ModulePackageStartupGuard.Complete(root);

            var quarantineOperation = ModulePackageOperationStore.Record(root, "install", fixtureRecord, null, now);
            ModulePackageStartupGuard.Begin(root, "quarantine-a");
            ModulePackageStartupGuard.Begin(root, "quarantine-b");
            var quarantined = InstalledModuleStore.Read(root).Single(module => module.ModuleId == "fixture-module");
            var quarantinedOperation = ModulePackageOperationStore.Read(root)
                .Single(operation => operation.OperationId == quarantineOperation.OperationId);
            check(quarantined.Quarantined && quarantinedOperation.State == ModulePackageOperationState.Failed
                && quarantinedOperation.ProblemCode == "module-package-operation-startup-quarantined",
                "a first install that never reached ApplicationStarted is quarantined instead of boot looping");
            var quarantinedModules = InstalledModuleCatalog.Load(root);
            var quarantinedModule = quarantinedModules.Single(module => module.Id == "fixture-module");
            var quarantineBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = Environments.Development });
            ServerModulesCatalog.CreateManaged(new HashSet<string>(StringComparer.Ordinal) { "fixture-module" }, quarantinedModules)
                .ConfigureServices(quarantineBuilder, ServerModuleContext.Create(quarantineBuilder.Configuration, quarantineBuilder.Environment, "memory"));
            check(quarantinedModule.Capabilities.Count == 0
                && !quarantineBuilder.Services.Any(descriptor => descriptor.ServiceType.FullName == "FixtureModule.FixtureMarker"),
                "a quarantined module publishes no capability and loads no assembly");
            ModulePackageStartupGuard.Complete(root);
            var restoredModules = InstalledModuleStore.Read(root).ToList();
            restoredModules[restoredModules.FindIndex(module => module.ModuleId == "fixture-module")] = fixtureRecord;
            InstalledModuleStore.Write(root, restoredModules);

            var fixtureEntryPath = Path.Combine(root, "data", "module-management", fixtureRecord.PackagePath, fixtureRecord.EntryAssembly);
            var originalFixture = await File.ReadAllBytesAsync(fixtureEntryPath);
            var tamperedFixture = originalFixture.ToArray();
            tamperedFixture[0] ^= 1;
            await File.WriteAllBytesAsync(fixtureEntryPath, tamperedFixture);
            var tampered = ServerModulesCatalog.CreateManaged(new HashSet<string>(StringComparer.Ordinal) { "fixture-module" },
                InstalledModuleCatalog.Load(root));
            var tamperedBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = Environments.Development });
            try
            {
                tampered.ConfigureServices(tamperedBuilder, ServerModuleContext.Create(tamperedBuilder.Configuration, tamperedBuilder.Environment, "memory"));
                throw new Exception("tampered installed module was accepted");
            }
            catch (ModulePackageException error) when (error.ProblemCode == "module-package-installed-integrity")
            {
                check(true, "installed module bytes are rehashed before assembly load");
            }
            finally { await File.WriteAllBytesAsync(fixtureEntryPath, originalFixture); }

            var loaded = InstalledModuleCatalog.Load(root);
            var fixtureModules = ServerModulesCatalog.CreateManaged(new HashSet<string>(StringComparer.Ordinal) { "fixture-module" }, loaded);
            var moduleBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = root,
                EnvironmentName = Environments.Development,
            });
            var moduleContext = ServerModuleContext.Create(moduleBuilder.Configuration, moduleBuilder.Environment, "memory");
            fixtureModules.ConfigureServices(moduleBuilder, moduleContext);
            check(moduleBuilder.Services.Any(descriptor => descriptor.ServiceType.FullName == "FixtureModule.FixtureMarker"),
                "installed module assembly loads through the Host ABI and registers its services");
            using var moduleApp = moduleBuilder.Build();
            fixtureModules.MapEndpoints(moduleApp, moduleContext);
            var routes = ((IEndpointRouteBuilder)moduleApp).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
            check(routes.Contains("/api/v1.0/fixture-module/ping", StringComparer.Ordinal),
                "installed module maps endpoints through the Host ABI");

            InstalledModuleStore.Remove(root, "example", deleteFiles: false);
            InstalledModuleStore.Remove(root, "fixture-module", deleteFiles: false);
            check(InstalledModuleStore.Read(root).Count == 0
                && Directory.Exists(Path.Combine(root, "data", "module-management", record.PackagePath)),
                "remove-installed removes the record and preserves package files by default");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
