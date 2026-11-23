using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Terminal;

internal static class ModulePackageBuilderChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "module-builder-" + Guid.NewGuid().ToString("N"));
        var publish = Path.Combine(root, "publish");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(publish);
        Directory.CreateDirectory(output);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "FixtureModule.dll");
            File.Copy(fixture, Path.Combine(publish, "FixtureModule.dll"));
            await File.WriteAllTextAsync(Path.Combine(publish, "asset.txt"), "module asset");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            const string source = "ModulePackages:Sources:official-modules";
            var defaultConfiguration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false).Build();
            check(defaultConfiguration.GetValue<bool?>(source + ":Enabled") == false
                && !defaultConfiguration.GetSection(source + ":PublicKeysSpki").Exists(),
                "the published official module source requires explicit enablement and an operator-provisioned key");
            var provisioned = new ConfigurationBuilder().AddConfiguration(defaultConfiguration)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [source + ":Enabled"] = "true",
                    [source + ":PublicKeysSpki:fixture"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                }).Build();
            var official = ModulePackageHostConfiguration.Source(provisioned, "official-modules");
            check(official.Enabled && official.Revision == 10 && official.AuthorizedModuleIds.ToHashSet(StringComparer.Ordinal).SetEquals(
                ["cloud-storage", "cloud-webdav", "cloud-s3", "cloud-baidu", ServerModules.DatabaseId,
                    "relaxkonos.tailscale", ServerModules.FirewallId, ServerModules.GitId, ServerModules.DockerId,
                    ServerModules.WebServerId, ServerModules.CertificatesId, ServerModules.ProxyId, "relaxkonos.tunnels", ServerModules.ApplicationDeploymentsId,
                    ServerModules.SystemPerformanceId, ServerModules.TerminalId]),
                "the provisioned official source authorizes every supported signed execution package and the existing modules");
            var keyPath = Path.Combine(root, "signing-key.pem");
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            var rid = RelaxKonOS.ModulePackaging.ModulePackagePlatform.CurrentRid;
            var now = DateTimeOffset.UtcNow;
            var options = new ModulePackageBuildOptions(publish, output, "fixture-builder", "1.0.0",
                "fixture-builder-1.0.0-" + rid, "fixture", "official", rid, "FixtureModule.dll",
                "FixtureModule.FixtureInstalledModule", "fixture-build", new string('a', 40), "official", 1,
                "https://modules.example/", keyPath, "builder-key", 3600);
            var wrongOutput = Path.Combine(root, "wrong-entry");
            try
            {
                await ModulePackageBuilder.BuildAsync(options with
                {
                    OutputDirectory = wrongOutput, EntryType = "FormerAssembly.FixtureInstalledModule",
                }, now);
                throw new InvalidOperationException("Expected a missing entry type to be rejected.");
            }
            catch (ModulePackageException error) when (error.ProblemCode == "module-package-build-entry-type-not-found")
            {
                check(!Directory.EnumerateFiles(wrongOutput).Any(), "a missing entry type produces no signed artifacts");
            }
            var result = await ModulePackageBuilder.BuildAsync(options, now);
            var terminal = result.Manifest with
            {
                ModuleId = ServerModules.TerminalId,
                Runtime = "in-process",
                EntryType = BundledTerminalPtyFactory.EntryType,
                EntryAssembly = $"server/{rid}/{BundledTerminalPtyFactory.AssemblyFile}",
                Capabilities = [ServerCapabilities.Terminal],
            };
            var terminalEntry = Path.Combine(AppContext.BaseDirectory, "Modules", "Terminal", BundledTerminalPtyFactory.AssemblyFile);
            InstalledModuleInstaller.VerifyExecutionEntry(terminalEntry, terminal);
            check(InstalledTerminalModule.MatchesManifest(terminal) && !ServerModules.ConflictsWithBundledModule(terminal),
                "the official Terminal execution entry can replace the bundled module");
            try
            {
                InstalledModuleInstaller.VerifyExecutionEntry(fixture, terminal);
                check(false, "an unrelated assembly must not pass the Terminal execution check");
            }
            catch (ModulePackageException error) when (error.ProblemCode == "module-package-execution-contract")
            {
                check(true, "the Terminal package refuses an unrelated execution assembly");
            }
            foreach (var target in new[] { "linux-x64", "win-x64" })
            {
                var performance = result.Manifest with
                {
                    ModuleId = ServerModules.SystemPerformanceId,
                    Runtime = "in-process",
                    Rid = target,
                    EntryType = RelaxKonOS.Server.SystemPerformance.BundledPerformanceSource.EntryType(target == "win-x64"),
                    EntryAssembly = $"server/{target}/RelaxKonOS.ServerModule.SystemPerformance.dll",
                    Capabilities = [ServerCapabilities.Metrics, ServerCapabilities.Processes],
                };
                var execution = Path.Combine(AppContext.BaseDirectory, "Modules", "SystemPerformance",
                    "RelaxKonOS.ServerModule.SystemPerformance.dll");
                InstalledModuleInstaller.VerifyExecutionEntry(execution, performance);
                check(InstalledSystemPerformanceModule.MatchesManifest(performance),
                    $"the official performance package accepts the actual {target} execution contract");
                try
                {
                    InstalledModuleInstaller.VerifyExecutionEntry(fixture, performance);
                    check(false, "an unrelated assembly must not pass the performance execution check");
                }
                catch (ModulePackageException error) when (error.ProblemCode == "module-package-execution-contract")
                {
                    check(true, $"the {target} performance package refuses an unrelated execution assembly");
                }
            }
            check(File.Exists(result.ArchivePath) && File.Exists(result.CatalogPath)
                && File.Exists(Path.Combine(output, "manifest.json")) && File.Exists(Path.Combine(output, "public-key.spki")),
                "module builder writes archive catalog manifest and public key");
            check(result.ArchiveSha256.Length == 64 && result.ManifestSha256.Length == 64
                && result.ArchiveBytes == new FileInfo(result.ArchivePath).Length,
                "module builder reports exact archive and manifest identities");
            check(result.Manifest.Files.Any(file => file.Path == "server/" + rid + "/FixtureModule.dll")
                && result.Manifest.Files.Any(file => file.Path == "server/" + rid + "/asset.txt"),
                "module builder includes every published file under the RID root");

            var policy = new ModulePackageTrustPolicy(1, "official", 1, true, "fixture", "official",
                "https://modules.example/", ["fixture-builder"],
                new Dictionary<string, string> { ["builder-key"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }, 3600);
            var context = new ModulePackageContext("builder-service", rid, "1.0.0", false);
            await using var archive = File.OpenRead(result.ArchivePath);
            var candidate = await ModulePackageVerifier.VerifyCandidateAsync(archive, File.ReadAllBytes(result.CatalogPath),
                policy, context, ModulePackageTrustHistory.Empty(policy, context), options.ReleaseId, now);
            check(candidate.Manifest.ModuleId == "fixture-builder" && candidate.ArchiveSha256 == result.ArchiveSha256,
                "module builder output verifies through the production signed-candidate path");

            var cliOutput = new StringWriter();
            var original = Console.Out;
            int exit;
            string[] arguments = ["modules", "pack-package",
                "--publish-dir", publish, "--output-dir", Path.Combine(root, "cli-output"),
                "--module-id", "fixture-cli", "--version", "1.0.0", "--release-id", "fixture-cli-1.0.0-" + rid,
                "--publisher-id", "fixture", "--purpose", "official", "--rid", rid,
                "--entry-assembly", "FixtureModule.dll", "--entry-type", "FixtureModule.FixtureInstalledModule",
                "--build-id", "fixture-build", "--source-commit", new string('a', 40), "--source-id", "official",
                "--sequence", "1", "--artifact-origin", "https://modules.example/", "--private-key", keyPath,
                "--key-id", "builder-key", "--validity-seconds", "3600"];
            try
            {
                Console.SetOut(cliOutput);
                exit = await ModulePackageBuildCommand.RunAsync(arguments);
            }
            finally { Console.SetOut(original); }
            using var cli = System.Text.Json.JsonDocument.Parse(cliOutput.ToString());
            check(exit == 0 && File.Exists(cli.RootElement.GetProperty("archivePath").GetString()!)
                && File.Exists(cli.RootElement.GetProperty("catalogPath").GetString()!),
                "pack-package CLI creates a verifiable archive and catalog without host state");
            var blockedOutput = Path.Combine(root, "cli-blocked");
            arguments[Array.IndexOf(arguments, "--output-dir") + 1] = blockedOutput;
            arguments[Array.IndexOf(arguments, "--module-id") + 1] = ServerModules.GitId;
            var originalError = Console.Error;
            using var blockedError = new StringWriter();
            try
            {
                Console.SetError(blockedError);
                exit = await ModulePackageBuildCommand.RunAsync(arguments);
            }
            finally { Console.SetError(originalError); }
            check(exit == 1 && blockedError.ToString().Trim() == ServerModules.PackageModuleIdConflict
                && !Directory.EnumerateFiles(blockedOutput).Any(),
                "pack-package rejects an incompatible bundled module replacement before writing signed artifacts");

            var legacyPublish = Directory.CreateDirectory(Path.Combine(root, "legacy-deployment")).FullName;
            File.Copy(typeof(ApplicationArchiveExtractor).Assembly.Location,
                Path.Combine(legacyPublish, ApplicationArchiveExecution.AssemblyFile));
            var legacyOutput = Path.Combine(root, "legacy-deployment-output");
            string[] deploymentArguments = [.. arguments, "--capabilities", ServerCapabilities.ApplicationDeployments];
            deploymentArguments[Array.IndexOf(deploymentArguments, "--publish-dir") + 1] = legacyPublish;
            deploymentArguments[Array.IndexOf(deploymentArguments, "--output-dir") + 1] = legacyOutput;
            deploymentArguments[Array.IndexOf(deploymentArguments, "--module-id") + 1] = ServerModules.ApplicationDeploymentsId;
            deploymentArguments[Array.IndexOf(deploymentArguments, "--entry-assembly") + 1] = ApplicationArchiveExecution.AssemblyFile;
            deploymentArguments[Array.IndexOf(deploymentArguments, "--entry-type") + 1] = ApplicationArchiveExecution.EntryType;
            using var legacyError = new StringWriter();
            try
            {
                Console.SetError(legacyError);
                exit = await ModulePackageBuildCommand.RunAsync(deploymentArguments);
            }
            finally { Console.SetError(originalError); }
            check(exit == 1 && legacyError.ToString().Trim() == "module-package-execution-contract"
                && !Directory.EnumerateFiles(legacyOutput).Any(),
                "pack-package refuses a legacy deployment entry before signing or writing an archive");

            deploymentArguments[Array.IndexOf(deploymentArguments, "--publish-dir") + 1] =
                Path.Combine(AppContext.BaseDirectory, "Modules", "ApplicationDeployments");
            using var deploymentOutput = new StringWriter();
            try
            {
                Console.SetOut(deploymentOutput);
                exit = await ModulePackageBuildCommand.RunAsync(deploymentArguments);
            }
            finally { Console.SetOut(original); }
            check(exit == 0 && Directory.EnumerateFiles(legacyOutput, "*.rkmodule").Count() == 1,
                "pack-package signs a deployment module that satisfies the current build context contract");

            var webAssembly = RelaxKonOS.Server.WebServer.WebServerExecutionCode.AssemblyFile;
            var webPublish = Directory.CreateDirectory(Path.Combine(root, "webserver-input")).FullName;
            var webEntry = Path.Combine(webPublish, webAssembly);
            var validWebCode = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Modules", "WebServer", webAssembly));
            var invalidWebCode = validWebCode.ToArray();
            var reloadEntryOffset = invalidWebCode.AsSpan().IndexOf("NginxReloadExecutor\0"u8);
            check(reloadEntryOffset >= 0, "the WebServer input contains the reload execution entry");
            invalidWebCode[reloadEntryOffset] = (byte)'X';
            await File.WriteAllBytesAsync(webEntry, invalidWebCode);
            var webOutput = Path.Combine(root, "webserver-output");
            string[] webArguments = [.. deploymentArguments, "--dependencies", "relaxkonos.certificates:1.0.0:2.0.0"];
            webArguments[Array.IndexOf(webArguments, "--publish-dir") + 1] = webPublish;
            webArguments[Array.IndexOf(webArguments, "--output-dir") + 1] = webOutput;
            webArguments[Array.IndexOf(webArguments, "--module-id") + 1] = ServerModules.WebServerId;
            webArguments[Array.IndexOf(webArguments, "--entry-assembly") + 1] = webAssembly;
            webArguments[Array.IndexOf(webArguments, "--entry-type") + 1] = RelaxKonOS.Server.WebServer.WebServerExecutionCode.EntryType;
            webArguments[Array.IndexOf(webArguments, "--capabilities") + 1] = ServerCapabilities.WebServer;
            using var webError = new StringWriter();
            try
            {
                Console.SetError(webError);
                exit = await ModulePackageBuildCommand.RunAsync(webArguments);
            }
            finally { Console.SetError(originalError); }
            check(exit == 1 && webError.ToString().Trim() == "module-package-execution-contract"
                && !Directory.EnumerateFiles(webOutput).Any(),
                "pack-package refuses WebServer without its reload executor despite a valid primary entry");

            await File.WriteAllBytesAsync(webEntry, validWebCode);
            using var webResult = new StringWriter();
            try
            {
                Console.SetOut(webResult);
                exit = await ModulePackageBuildCommand.RunAsync(webArguments);
            }
            finally { Console.SetOut(original); }
            check(exit == 0 && Directory.EnumerateFiles(webOutput, "*.rkmodule").Count() == 1,
                "pack-package signs the WebServer input when the reload contract is restored");

            var firstRelease = new ModulePackageRelease(result.Manifest, result.ManifestSha256, result.ArchiveSha256,
                result.ArchiveBytes, result.DownloadUri);
            var secondRelease = ModulePackageJson.Read<ModulePackageRelease>(System.Text.Encoding.UTF8.GetBytes(
                cli.RootElement.GetProperty("release").GetRawText()));
            var signingPolicy = policy with { AuthorizedModuleIds = [options.ModuleId, secondRelease.Manifest.ModuleId] };
            var aggregate = new ModulePackageCatalog(1, options.SourceId, 2, now, now.AddHours(1), [firstRelease, secondRelease], [], []);
            var payloadPath = Path.Combine(root, "aggregate-payload.json");
            var policyPath = Path.Combine(root, "aggregate-policy.json");
            var signedPath = Path.Combine(root, "aggregate-signed.json");
            await File.WriteAllBytesAsync(payloadPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(aggregate, ModulePackageJson.Options));
            await File.WriteAllBytesAsync(policyPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(signingPolicy, ModulePackageJson.Options));
            var artifacts = Directory.CreateDirectory(Path.Combine(root, "aggregate-artifacts")).FullName;
            var secondArchivePath = cli.RootElement.GetProperty("archivePath").GetString()!;
            File.Copy(result.ArchivePath, Path.Combine(artifacts, Path.GetFileName(result.ArchivePath)));
            string[] signArguments = ["modules", "sign-catalog", "--payload", payloadPath, "--policy", policyPath,
                "--private-key", keyPath, "--key-id", options.KeyId, "--artifact-dir", artifacts, "--output", signedPath];
            exit = await ModuleMaintenanceCommand.RunAsync(signArguments);
            check(exit == 1 && !File.Exists(signedPath), "aggregate signing refuses to publish if an archive is missing");
            var stagedArchive = Path.Combine(artifacts, Path.GetFileName(secondArchivePath));
            var archiveBytes = await File.ReadAllBytesAsync(secondArchivePath);
            archiveBytes[^1] ^= 1;
            await File.WriteAllBytesAsync(stagedArchive, archiveBytes);
            exit = await ModuleMaintenanceCommand.RunAsync(signArguments);
            check(exit == 1 && !File.Exists(signedPath), "aggregate signing refuses an archive with a changed digest");
            File.Copy(secondArchivePath, stagedArchive, overwrite: true);
            exit = await ModuleMaintenanceCommand.RunAsync(signArguments);
            var signedCatalog = await File.ReadAllBytesAsync(signedPath);
            var signingHistory = ModulePackageTrustHistory.Empty(signingPolicy, context);
            await using var secondArchive = File.OpenRead(cli.RootElement.GetProperty("archivePath").GetString()!);
            var verifiedCliArtifact = await ModulePackageVerifier.VerifyCandidateAsync(secondArchive, signedCatalog,
                signingPolicy, context, signingHistory, secondRelease.Manifest.ReleaseId, DateTimeOffset.UtcNow);
            check(exit == 0 && ModulePackageVerifier.VerifyCatalog(signedCatalog, signingPolicy, context,
                    signingHistory, DateTimeOffset.UtcNow).Catalog.Releases.Length == 2
                && verifiedCliArtifact.ArchiveSha256 == secondRelease.ArchiveSha256,
                "the pack-package release descriptor can be signed in an aggregate and verifies against the actual archive");

            var obsoleteDatabase = firstRelease with
            {
                Manifest = firstRelease.Manifest with
                {
                    ModuleId = ServerModules.DatabaseId, Runtime = "module-host",
                    HostApiMinimum = "2.4.0", HostApiMaximumExclusive = "3.0.0",
                },
            };
            try
            {
                await ModulePackageBuilder.SignCatalogAsync(aggregate with { Releases = [obsoleteDatabase, secondRelease] },
                    options.KeyId, keyPath);
                check(false, "aggregate signing must reject the obsolete Databases ABI");
            }
            catch (ModulePackageException error) when (error.ProblemCode == "module-package-host-api-incompatible")
            {
                check(true, "aggregate signing rejects an obsolete Databases ABI even when its archive already exists");
            }

            var catalogBytes = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
            using var catalogHandler = new FixedHandler(catalogBytes);
            var fetched = await ModulePackageCatalogFetcher.FetchAsync("https://modules.example/catalog.json", catalogHandler);
            check(fetched.SequenceEqual(catalogBytes), "catalog fetch accepts a bounded HTTPS response");
            using var redirectHandler = new FixedHandler([], System.Net.HttpStatusCode.Redirect);
            try
            {
                await ModulePackageCatalogFetcher.FetchAsync("https://modules.example/catalog.json", redirectHandler);
                check(false, "catalog fetch rejects redirects");
            }
            catch (ModulePackageException error)
            {
                check(error.ProblemCode == "module-catalog-fetch-failed", "catalog fetch rejects redirects");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class FixedHandler(byte[] body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}
