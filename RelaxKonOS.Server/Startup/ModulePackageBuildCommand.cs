using System.Text.Json;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Offline build tool for an official `.rkmodule` and its signed catalog. No Host state is read or written.</summary>
internal static class ModulePackageBuildCommand
{
    private sealed record BuildMetadata(ModulePackagePrivilege[] Privileges, ModulePackageUi Ui);
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
                capabilities, dependencies, runtime, metadata?.Privileges, metadata?.Ui);
            var result = await ModulePackageBuilder.BuildAsync(options, DateTimeOffset.UtcNow);
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
