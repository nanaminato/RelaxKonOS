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
            var keyPath = Path.Combine(root, "signing-key.pem");
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            var rid = RuntimeInformation.RuntimeIdentifier;
            var now = DateTimeOffset.UtcNow;
            var options = new ModulePackageBuildOptions(publish, output, "fixture-builder", "1.0.0",
                "fixture-builder-1.0.0-" + rid, "fixture", "official", rid, "FixtureModule.dll",
                "FixtureModule.FixtureInstalledModule", "fixture-build", new string('a', 40), "official", 1,
                "https://modules.example/", keyPath, "builder-key", 3600);
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
            try
            {
                Console.SetOut(cliOutput);
                exit = await ModulePackageBuildCommand.RunAsync(["modules", "pack-package",
                    "--publish-dir", publish, "--output-dir", Path.Combine(root, "cli-output"),
                    "--module-id", "fixture-cli", "--version", "1.0.0", "--release-id", "fixture-cli-1.0.0-" + rid,
                    "--publisher-id", "fixture", "--purpose", "official", "--rid", rid,
                    "--entry-assembly", "FixtureModule.dll", "--entry-type", "FixtureModule.FixtureInstalledModule",
                    "--build-id", "fixture-build", "--source-commit", new string('a', 40), "--source-id", "official",
                    "--sequence", "1", "--artifact-origin", "https://modules.example/", "--private-key", keyPath,
                    "--key-id", "builder-key", "--validity-seconds", "3600"]);
            }
            finally { Console.SetOut(original); }
            using var cli = System.Text.Json.JsonDocument.Parse(cliOutput.ToString());
            check(exit == 0 && File.Exists(cli.RootElement.GetProperty("archivePath").GetString()!)
                && File.Exists(cli.RootElement.GetProperty("catalogPath").GetString()!),
                "pack-package CLI creates a verifiable archive and catalog without host state");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
