using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Startup;

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
            check(official.Enabled && official.Revision == 8 && official.AuthorizedModuleIds.ToHashSet(StringComparer.Ordinal).SetEquals(
                ["cloud-storage", "cloud-webdav", "cloud-s3", "cloud-baidu", ServerModules.DatabaseId,
                    "relaxkonos.tailscale", ServerModules.FirewallId, ServerModules.GitId, ServerModules.DockerId,
                    ServerModules.WebServerId, ServerModules.CertificatesId, ServerModules.ProxyId, "relaxkonos.tunnels", ServerModules.ApplicationDeploymentsId]),
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
            exit = await ModuleMaintenanceCommand.RunAsync(["modules", "sign-catalog", "--payload", payloadPath,
                "--policy", policyPath, "--private-key", keyPath, "--key-id", options.KeyId, "--output", signedPath]);
            var signedCatalog = await File.ReadAllBytesAsync(signedPath);
            var signingHistory = ModulePackageTrustHistory.Empty(signingPolicy, context);
            await using var secondArchive = File.OpenRead(cli.RootElement.GetProperty("archivePath").GetString()!);
            var verifiedCliArtifact = await ModulePackageVerifier.VerifyCandidateAsync(secondArchive, signedCatalog,
                signingPolicy, context, signingHistory, secondRelease.Manifest.ReleaseId, DateTimeOffset.UtcNow);
            check(exit == 0 && ModulePackageVerifier.VerifyCatalog(signedCatalog, signingPolicy, context,
                    signingHistory, DateTimeOffset.UtcNow).Catalog.Releases.Length == 2
                && verifiedCliArtifact.ArchiveSha256 == secondRelease.ArchiveSha256,
                "the pack-package release descriptor can be signed in an aggregate and verifies against the actual archive");

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
