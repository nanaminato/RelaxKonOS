using System.IO.Compression;
using System.Text.Json;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Developer;
using RelaxKonOS.Server.Startup;

internal static class OfficialDesktopModuleContractChecks
{
    internal static async Task RunAsync(string packageDirectory)
    {
        var inventory = await ReadBundledInventoryAsync();
        using var profiles = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(packageDirectory, "installation-profiles.json")));
        var offlineApps = profiles.RootElement.GetProperty("profiles").EnumerateObject()
            .SelectMany(profile => profile.Value.EnumerateArray())
            .Where(entry => entry.GetProperty("installPolicy").GetString() == "offline")
            .Select(entry => entry.GetProperty("appId").GetString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var appId in OfficialDesktopCatalogRules.ApplicationIds)
        {
            using var archive = ZipFile.OpenRead(Path.Combine(packageDirectory, appId + ".roapp"));
            using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            using var manifest = await JsonDocument.ParseAsync(manifestStream);
            if (offlineApps.Contains(appId) && manifest.RootElement.TryGetProperty("serverRequirements", out var serverRequirements))
            {
                var requirements = serverRequirements.Deserialize<ApplicationServerRequirements>(RelaxKonOSJsonOptions.Default);
                if (requirements is { RequiredCapabilities.Count: > 0 } or { SupportedPlatforms.Count: > 0 }
                    && !JointAppDeploymentService.HasSupportedBundledLaunchRequirements(appId, requirements))
                    throw new InvalidDataException($"{appId} has server launch requirements that prevent offline provisioning.");
            }
            if (!manifest.RootElement.TryGetProperty("dependencies", out var dependencies))
                continue;
            var declared = dependencies.Deserialize<PackageDeploymentDependencies>(RelaxKonOSJsonOptions.Default);
            DeveloperPackageManager.ValidateDependencies(declared?.Required, optional: false);
            DeveloperPackageManager.ValidateDependencies(declared?.Optional, optional: true);
            foreach (var requirement in (declared?.Required ?? []).Concat(declared?.Optional ?? []))
            {
                if (requirement.Package.Kind != DeploymentPackageKind.ServerModule)
                    continue;
                if (!inventory.TryGetValue(requirement.Package.Id, out var module)
                    || (requirement.Capability is { } capability
                        && (!module.Capabilities.Contains(capability, StringComparer.Ordinal)
                            || (requirement.ContractVersion is { } version
                                && module.CapabilityContractVersions?.GetValueOrDefault(capability) != version))))
                    throw new InvalidDataException($"{appId} declares {requirement.Package.Id} with {requirement.Capability} {requirement.ContractVersion}, which the bundled inventory does not provide.");
                if (requirement is { MinimumVersion: not null } or { MaximumVersionExclusive: not null }
                    && (ModuleDeploymentCatalog.InstalledActivationVersion(module) is not { } bundledVersion
                        || !DeploymentResolver.IsVersionAllowed(bundledVersion, requirement)))
                    throw new InvalidDataException($"{appId} declares a version range for {module.Id} that the bundled version {module.InstalledVersion ?? "(unavailable)"} does not satisfy.");
            }
        }
        Console.WriteLine($"Verified bundled server module contracts for {OfficialDesktopCatalogRules.ApplicationIds.Count} official desktop archives.");
    }

    private static async Task<Dictionary<string, ServerModuleDto>> ReadBundledInventoryAsync()
    {
        var root = Directory.CreateTempSubdirectory("rk-bundled-contracts-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.FullName });
            using var services = new ServiceCollection().BuildServiceProvider();
            var modules = ServerModules.CreateManaged(new HashSet<string>());
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            return (await modules.DescribeAsync(services, context, default))
                .ToDictionary(module => module.Id, StringComparer.Ordinal);
        }
        finally { root.Delete(); }
    }
}
