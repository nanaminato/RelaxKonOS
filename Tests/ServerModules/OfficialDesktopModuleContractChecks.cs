using System.IO.Compression;
using System.Text.Json;
using RelaxKonOS.Deployment;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Developer;
using RelaxKonOS.Server.Startup;

internal static class OfficialDesktopModuleContractChecks
{
    internal static async Task RunAsync(string packageDirectory)
    {
        var builder = WebApplication.CreateBuilder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var modules = ServerModules.CreateManaged(new HashSet<string>());
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        var inventory = (await modules.DescribeAsync(services, context, default))
            .ToDictionary(module => module.Id, StringComparer.Ordinal);

        foreach (var appId in OfficialDesktopCatalogRules.ApplicationIds)
        {
            using var archive = ZipFile.OpenRead(Path.Combine(packageDirectory, appId + ".roapp"));
            using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            using var manifest = await JsonDocument.ParseAsync(manifestStream);
            if (!manifest.RootElement.TryGetProperty("dependencies", out var dependencies)
                || !dependencies.TryGetProperty("required", out var required))
                continue;
            foreach (var entry in required.EnumerateArray())
            {
                var requirement = entry.Deserialize<DeploymentRequirement>(RelaxKonOSJsonOptions.Default)!;
                if (requirement.Package.Kind != DeploymentPackageKind.ServerModule)
                    continue;
                if (!inventory.TryGetValue(requirement.Package.Id, out var module)
                    || (requirement.Capability is { } capability
                        && (!module.Capabilities.Contains(capability, StringComparer.Ordinal)
                            || (requirement.ContractVersion is { } version
                                && module.CapabilityContractVersions?.GetValueOrDefault(capability) != version))))
                    throw new InvalidDataException($"{appId} requires {requirement.Package.Id} with {requirement.Capability} {requirement.ContractVersion}, which the bundled inventory does not provide.");
            }
        }
        Console.WriteLine($"Verified bundled server module contracts for {OfficialDesktopCatalogRules.ApplicationIds.Count} official desktop archives.");
    }
}
