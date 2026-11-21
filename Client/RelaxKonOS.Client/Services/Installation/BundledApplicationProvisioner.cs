using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Core.Applications;
using Drive = RelaxKonOS.Client.Services.VirtualSystemDrive.VirtualSystemDrive;

namespace RelaxKonOS.Client.Services.AppPackages;

/// <summary>Provisions the offline defaults declared by the desktop distribution once per app ID.</summary>
internal sealed class BundledApplicationProvisioner(DeveloperPackageManager packages, AppPackageInstallerService installer, Drive drive)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var manifest = packages.OfficialSource.BundledDefaultsPath;
        if (!File.Exists(manifest)) return;

        try
        {
            if (new FileInfo(manifest).Length > 8192 || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Invalid bundled application defaults.");

            var ids = await File.ReadAllLinesAsync(manifest, cancellationToken);
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !OfficialDesktopPackages.IsExternalOfficialId(id)))
                throw new InvalidDataException("Invalid bundled application defaults.");

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var receipt = drive.ResolveRootChild($"System/bundled-apps/{id}.json");
                    if (File.Exists(receipt)) continue;
                    var existing = packages.FindInstalled(id);
                    if (existing is null)
                    {
                        var path = packages.OfficialSource.BundledPath(id);
                        var candidate = await installer.InspectLocalAsync(path, cancellationToken: cancellationToken);
                        if (candidate.Manifest.Id != id || candidate.ExpectedSha256 is null)
                            throw new InvalidDataException("Invalid bundled application identity.");

                        var platforms = ApplicationPlatformNames.Normalize(candidate.Manifest.ClientPlatforms);
                        var requirements = candidate.Manifest.ServerRequirements;
                        if (platforms.Count > 0 && !platforms.Contains(ApplicationCompatibilityService.DetectClientPlatform(), StringComparer.Ordinal)
                            || requirements is { RequiredCapabilities.Count: > 0 } or { SupportedPlatforms.Count: > 0 }
                            || candidate.Manifest.Dependencies?.Required is { Count: > 0 })
                            throw new InvalidOperationException("The bundled application requires an interactive deployment.");

                        existing = await installer.InstallAsync(candidate, cancellationToken);
                    }

                    await drive.WriteJsonAtomicallyAsync(receipt, new ProvisionedApplication(id, existing.Version), cancellationToken);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && AppPackageInstallerService.IsStoreFailure(error))
                {
                    packages.RecordActivationDiagnostic($"Bundled application {id} could not be provisioned: {error.GetType().Name}.");
                }
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && AppPackageInstallerService.IsStoreFailure(error))
        {
            packages.RecordActivationDiagnostic($"Bundled application defaults could not be read: {error.GetType().Name}.");
        }
    }

    private sealed record ProvisionedApplication(string Id, string Version);
}
