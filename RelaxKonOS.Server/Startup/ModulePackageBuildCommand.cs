using System.Text.Json;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Offline build tool for an official `.rkmodule` and its signed catalog. No Host state is read or written.</summary>
internal static class ModulePackageBuildCommand
{
    private sealed record BuildMetadata(ModulePackageCapability[] Privileges, ModulePackageUi Ui,
        IReadOnlyDictionary<string, string>? CapabilityContractVersions = null);
    private static readonly string[] Required =
    [
        "--publish-dir", "--output-dir", "--module-id", "--version", "--release-id", "--publisher-id",
        "--purpose", "--rid", "--entry-assembly", "--entry-type", "--build-id", "--source-commit",
        "--source-id", "--sequence", "--artifact-origin", "--private-key", "--key-id", "--validity-seconds",
    ];
    private static readonly string[] Optional =
    [
        "--host-api-min", "--host-api-max", "--capabilities", "--dependencies", "--runtime", "--metadata",
    ];

    internal static async Task<int> SignCatalogAsync(string[] args)
    {
        string[] required = ["--payload", "--policy", "--private-key", "--key-id", "--output"];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            for (var index = 2; index < args.Length; index += 2)
                if (index + 1 >= args.Length || !required.Contains(args[index], StringComparer.Ordinal)
                    || !values.TryAdd(args[index], args[index + 1]))
                    throw new ModulePackageException("module-package-build-command-invalid");
            if (values.Count != required.Length) throw new ModulePackageException("module-package-build-command-invalid");
            var catalog = await ReadInputAsync<ModulePackageCatalog>(values["--payload"]);
            var policy = await ReadInputAsync<ModulePackageTrustPolicy>(values["--policy"]);
            if (policy.Purpose != "official" || catalog.Releases is null)
                throw new ModulePackageException("module-package-build-command-invalid");
            foreach (var release in catalog.Releases)
            {
                if (release?.Manifest is not { } manifest) throw new ModulePackageException("module-package-release-invalid");
                ModulePackageVerifier.ValidateManifest(manifest);
                if (ServerModules.ConflictsWithBundledModule(manifest))
                    throw new ModulePackageException(ServerModules.PackageModuleIdConflict);
            }
            var signed = await ModulePackageBuilder.SignCatalogAsync(catalog, values["--key-id"], values["--private-key"]);
            var context = new ModulePackageContext("offline-release-validation", ModulePackagePlatform.CurrentRid,
                ModulePackageHostConfiguration.HostApiVersion, false);
            _ = ModulePackageVerifier.VerifyCatalog(signed.Envelope, policy, context,
                ModulePackageTrustHistory.Empty(policy, context), DateTimeOffset.UtcNow);
            await using var output = new FileStream(values["--output"], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(signed.Envelope);
            return 0;
        }
        catch (ModulePackageException exception)
        {
            Console.Error.WriteLine(exception.ProblemCode);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("module-package-build-failed");
            return 1;
        }

        static async Task<T> ReadInputAsync<T>(string path)
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length is <= 0 or > 4 * 1024 * 1024)
                throw new ModulePackageException("module-package-build-command-invalid");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes);
            return ModulePackageJson.Read<T>(bytes);
        }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 2; i < args.Length; i += 2)
                if (i + 1 >= args.Length || !Required.Contains(args[i], StringComparer.Ordinal)
                        && !Optional.Contains(args[i], StringComparer.Ordinal)
                    || !values.TryAdd(args[i], args[i + 1]))
                    throw new ModulePackageException("module-package-build-command-invalid");
            if (Required.Any(key => !values.ContainsKey(key)))
                throw new ModulePackageException("module-package-build-command-invalid");
            if (!long.TryParse(values["--sequence"], out var sequence) || sequence <= 0
                || !int.TryParse(values["--validity-seconds"], out var validity) || validity is <= 0 or > 2678400)
                throw new ModulePackageException("module-package-build-command-invalid");
            var capabilities = values.TryGetValue("--capabilities", out var capabilityText)
                ? capabilityText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            var dependencies = new List<ModulePackageDependency>();
            if (values.TryGetValue("--dependencies", out var dependencyText))
                foreach (var entry in dependencyText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = entry.Split(':');
                    if (parts.Length != 3) throw new ModulePackageException("module-package-build-dependency-invalid");
                    dependencies.Add(new(parts[0], parts[1], parts[2]));
                }
            var runtime = values.GetValueOrDefault("--runtime", "in-process");
            var metadata = values.TryGetValue("--metadata", out var metadataPath)
                ? ModulePackageJson.Read<BuildMetadata>(await File.ReadAllBytesAsync(metadataPath)) : null;
            var options = new ModulePackageBuildOptions(
                values["--publish-dir"], values["--output-dir"], values["--module-id"], values["--version"],
                values["--release-id"], values["--publisher-id"], values["--purpose"], values["--rid"],
                values["--entry-assembly"], values["--entry-type"], values["--build-id"], values["--source-commit"],
                values["--source-id"], sequence, values["--artifact-origin"], values["--private-key"], values["--key-id"],
                validity,
                values.GetValueOrDefault("--host-api-min", runtime == "module-host" ? "2.0.0" : "1.0.0"),
                values.GetValueOrDefault("--host-api-max", runtime == "module-host" ? "3.0.0" : "2.0.0"),
                capabilities, dependencies, runtime, metadata?.Privileges, metadata?.Ui, metadata?.CapabilityContractVersions);
            var result = await ModulePackageBuilder.BuildAsync(options, DateTimeOffset.UtcNow, validateManifest: manifest =>
            {
                if (ServerModules.ConflictsWithBundledModule(manifest))
                    throw new ModulePackageException(ServerModules.PackageModuleIdConflict);
            });
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                archivePath = result.ArchivePath,
                catalogPath = result.CatalogPath,
                manifestSha256 = result.ManifestSha256,
                archiveSha256 = result.ArchiveSha256,
                archiveBytes = result.ArchiveBytes,
                publicKeySpki = result.PublicKeySpkiBase64,
            }, ModulePackageJson.Options));
            return 0;
        }
        catch (ModulePackageException exception)
        {
            Console.Error.WriteLine(exception.ProblemCode);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("module-package-build-failed");
            return 1;
        }
    }
}
