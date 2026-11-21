using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.ManagedRuntimes;
using Drive = RelaxKonOS.Client.Services.VirtualSystemDrive.VirtualSystemDrive;

namespace RelaxKonOS.Client.Services.AppPackages;

/// <summary>Provisions the selected desktop distribution profile once per app ID.</summary>
internal sealed class BundledApplicationProvisioner(DeveloperPackageManager packages, AppPackageInstallerService installer,
    JointAppDeploymentService deployment, Drive drive)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task RunAsync(IEnumerable<string>? arguments = null, CancellationToken cancellationToken = default)
    {
        var manifest = packages.OfficialSource.BundledProfilesPath;
        if (!File.Exists(manifest)) return;

        try
        {
            if (new FileInfo(manifest).Length > 8192 || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Invalid bundled application defaults.");

            await using var input = File.OpenRead(manifest);
            var profiles = await JsonSerializer.DeserializeAsync<ProfileDocument>(input, Json, cancellationToken);
            if (profiles is not { SchemaVersion: 2, DefaultProfile: "standard", Profiles.Count: >= 3 and <= 16 }
                || !new[] { "minimal", "standard", "developer" }.All(profiles.Profiles.ContainsKey)
                || profiles.Profiles.Values.Any(entries => !ValidEntries(entries)))
                throw new InvalidDataException("Invalid bundled application profiles.");

            var selectionPath = drive.ResolveRootChild("System/installation-profile.json");
            var saved = File.Exists(selectionPath) ? await drive.ReadJsonAsync<ProfileSelection>(selectionPath, cancellationToken) : null;
            var selection = SelectProfile(profiles, saved, arguments ?? []);
            if (selection != saved)
                await drive.WriteJsonAtomicallyAsync(selectionPath, selection, cancellationToken);

            foreach (var entry in profiles.Profiles[selection.Profile])
            {
                var id = entry.AppId;
                if (entry.InstallPolicy == "interactive" || selection.Excluded.Contains(id, StringComparer.Ordinal)) continue;
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
                        if (!MeetsVersion(candidate.Manifest.Version, entry.MinimumVersion))
                            throw new InvalidDataException("The bundled release does not meet the profile version.");

                        var platforms = ApplicationPlatformNames.Normalize(candidate.Manifest.ClientPlatforms);
                        var requirements = candidate.Manifest.ServerRequirements;
                        if (platforms.Count > 0 && !platforms.Contains(ApplicationCompatibilityService.DetectClientPlatform(), StringComparer.Ordinal)
                            || (requirements is { RequiredCapabilities.Count: > 0 } or { SupportedPlatforms.Count: > 0 }
                                && !JointAppDeploymentService.AllowsBundledLaunchRequirements(candidate, packages))
                            || candidate.Manifest.Dependencies?.Required is { Count: > 0 })
                            throw new InvalidOperationException("The bundled application requires an interactive deployment.");

                        var review = await deployment.FindPendingAsync(candidate, cancellationToken, localOnly: true)
                            ?? await deployment.PrepareAsync(candidate, [], cancellationToken);
                        existing = await deployment.ExecuteAsync(review, cancellationToken);
                    }
                    else if (!MeetsVersion(existing.Version, entry.MinimumVersion))
                        throw new InvalidOperationException("The installed application needs an interactive update.");

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

    private static bool ValidEntries(ProfileApplication[]? entries) => entries is { Length: <= 128 }
        && entries.All(entry => entry is not null && OfficialDesktopPackages.IsExternalOfficialId(entry.AppId)
            && entry.InstallPolicy is "offline" or "interactive"
            && RuntimeSemanticVersion.TryParse(entry.MinimumVersion, out _))
        && entries.Select(entry => entry.AppId).Distinct(StringComparer.Ordinal).Count() == entries.Length;

    private static bool MeetsVersion(string version, string minimum) =>
        RuntimeSemanticVersion.TryParse(version, out var release)
        && RuntimeSemanticVersion.TryParse(minimum, out var floor) && release.CompareTo(floor) >= 0;

    private static ProfileSelection SelectProfile(ProfileDocument document, ProfileSelection? saved, IEnumerable<string> arguments)
    {
        const string profileFlag = "--installation-profile=";
        const string excludeFlag = "--exclude-bundled-app=";
        var options = arguments.ToArray();
        var requested = options.Where(option => option.StartsWith(profileFlag, StringComparison.Ordinal))
            .Select(option => option[profileFlag.Length..]).ToArray();
        var exclusions = options.Where(option => option.StartsWith(excludeFlag, StringComparison.Ordinal))
            .Select(option => option[excludeFlag.Length..]).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length > 1) throw new InvalidDataException("Choose one installation profile.");
        var profile = requested.SingleOrDefault() ?? saved?.Profile ?? document.DefaultProfile;
        if (profile is null || !document.Profiles.TryGetValue(profile, out var entries)
            || exclusions.Any(id => !entries.Any(entry => entry.AppId == id)))
            throw new InvalidDataException("Invalid installation profile selection.");
        if (saved is not null && requested.Length == 0 && exclusions.Length == 0)
            return saved.Excluded is not null ? saved : throw new InvalidDataException("Invalid saved installation profile.");
        return new(profile, exclusions);
    }

    private sealed record ProfileDocument(int SchemaVersion, string DefaultProfile, Dictionary<string, ProfileApplication[]> Profiles);
    private sealed record ProfileApplication(string AppId, string MinimumVersion, string InstallPolicy);
    private sealed record ProfileSelection(string Profile, string[] Excluded);
    private sealed record ProvisionedApplication(string Id, string Version);
}
