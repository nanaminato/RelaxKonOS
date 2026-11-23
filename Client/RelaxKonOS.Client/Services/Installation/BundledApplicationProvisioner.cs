using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.VirtualSystemDrive;
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

    private string SelectionPath => drive.ResolveRootChild("System/installation-profile.json");

    internal async Task<IReadOnlyList<ProfileApplication>> ReadInteractiveChoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var selection = await ReadSelectionAsync(cancellationToken);
            if (selection is not { Profile.Length: > 0, Excluded: not null }) return [];
            var profiles = await ReadProfilesAsync(cancellationToken);
            return profiles is not null && profiles.Profiles.TryGetValue(selection.Profile, out var entries)
                ? entries.Where(entry => entry.InstallPolicy == "interactive"
                    && !selection.Excluded.Contains(entry.AppId, StringComparer.Ordinal)).ToArray()
                : [];
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && AppPackageInstallerService.IsStoreFailure(error))
        {
            packages.RecordActivationDiagnostic($"Bundled application choices could not be read: {error.GetType().Name}.");
            return [];
        }
    }

    public async Task RunAsync(IEnumerable<string>? arguments = null, CancellationToken cancellationToken = default,
        Func<ProfileDocument, IReadOnlyDictionary<string, ProfileArchive>, Task<ProfileSelection>>? chooseProfile = null)
    {
        try
        {
            var profiles = await ReadProfilesAsync(cancellationToken);
            if (profiles is null) return;
            var options = arguments?.ToArray() ?? [];
            var explicitProfile = options.Any(option => option.StartsWith(ProfileFlag, StringComparison.Ordinal));
            ProfileSelection? saved;
            try
            {
                saved = await ReadSelectionAsync(cancellationToken);
                if (saved is not null && saved is not { Profile.Length: > 0, Excluded: not null })
                    throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.JsonInvalid);
            }
            catch (VirtualSystemDriveException error) when (explicitProfile && error.ProblemCode == VirtualSystemDriveProblemCode.JsonInvalid)
            {
                packages.RecordActivationDiagnostic("Invalid installation profile will be replaced by the explicit selection.");
                saved = null;
            }
            var selection = saved is null && chooseProfile is not null
                && !explicitProfile && !options.Any(option => option.StartsWith(ExcludeFlag, StringComparison.Ordinal))
                ? await chooseProfile(profiles, await ReadBundledArchivesAsync(profiles, cancellationToken))
                : SelectProfile(profiles, saved, options);
            if (!profiles.Profiles.TryGetValue(selection.Profile, out var selectedEntries) || selection.Excluded is null)
                throw new InvalidDataException("Invalid installation profile selection.");
            if (selection != saved)
                await drive.WriteJsonAtomicallyAsync(SelectionPath, selection, cancellationToken);

            foreach (var entry in selectedEntries)
            {
                var id = entry.AppId;
                if (entry.InstallPolicy == "interactive" || selection.Excluded.Contains(id, StringComparer.Ordinal)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var receipt = drive.ResolveRootChild($"System/bundled-apps/{id}.json");
                    var existing = packages.FindInstalled(id);
                    if (existing is null && File.Exists(receipt)) continue;
                    if (existing is null || !MeetsVersion(existing.Version, entry.MinimumVersion))
                    {
                        var path = packages.OfficialSource.BundledPath(id);
                        var candidate = await installer.InspectLocalAsync(path, cancellationToken: cancellationToken);
                        if (candidate.Manifest.Id != id || candidate.ExpectedSha256 is null)
                            throw new InvalidDataException("Invalid bundled application identity.");
                        if (!MeetsVersion(candidate.Manifest.Version, entry.MinimumVersion))
                            throw new InvalidDataException("The bundled release does not meet the profile version.");

                        if (!CanProvisionOffline(candidate.Manifest, candidate.ExpectedSha256, candidate.LocalPath))
                            throw new InvalidOperationException("The bundled application requires an interactive deployment.");

                        var review = await deployment.FindPendingAsync(candidate, cancellationToken, localOnly: true)
                            ?? await deployment.PrepareAsync(candidate, [], cancellationToken);
                        existing = await deployment.ExecuteAsync(review, cancellationToken);
                    }
                    if (!File.Exists(receipt))
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

    private async Task<IReadOnlyDictionary<string, ProfileArchive>> ReadBundledArchivesAsync(ProfileDocument profiles, CancellationToken cancellationToken)
    {
        var archives = new Dictionary<string, ProfileArchive>(StringComparer.Ordinal);
        foreach (var id in profiles.Profiles.Values.SelectMany(entries => entries).Select(entry => entry.AppId).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var path = packages.OfficialSource.BundledPath(id);
                var (manifest, digest) = await packages.InspectAsync(path, cancellationToken);
                if (manifest.Id == id && digest is not null)
                    archives[id] = new(manifest.DisplayName, manifest.Version, CanProvisionOffline(manifest, digest, path));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && AppPackageInstallerService.IsStoreFailure(error))
            {
                packages.RecordActivationDiagnostic($"Bundled application {id} could not be inspected: {error.GetType().Name}.");
            }
        }
        return archives;
    }

    private bool CanProvisionOffline(DeveloperPackageManifest manifest, string? digest, string path)
    {
        var platforms = ApplicationPlatformNames.Normalize(manifest.ClientPlatforms);
        return (platforms.Count == 0 || platforms.Contains(ApplicationCompatibilityService.DetectClientPlatform(), StringComparer.Ordinal))
            && manifest.Dependencies?.Required is not { Count: > 0 }
            && (manifest.ServerRequirements is not ({ RequiredCapabilities.Count: > 0 } or { SupportedPlatforms.Count: > 0 })
                || JointAppDeploymentService.AllowsBundledLaunchRequirements(manifest, digest, path, packages));
    }

    private async Task<ProfileSelection?> ReadSelectionAsync(CancellationToken cancellationToken) =>
        File.Exists(SelectionPath) ? await drive.ReadJsonAsync<ProfileSelection>(SelectionPath, cancellationToken) : null;

    private async Task<ProfileDocument?> ReadProfilesAsync(CancellationToken cancellationToken)
    {
        var manifest = packages.OfficialSource.BundledProfilesPath;
        if (!File.Exists(manifest)) return null;
        if (new FileInfo(manifest).Length > 8192 || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid bundled application defaults.");
        await using var input = File.OpenRead(manifest);
        var profiles = await JsonSerializer.DeserializeAsync<ProfileDocument>(input, Json, cancellationToken);
        if (profiles is not { SchemaVersion: 2, DefaultProfile: "standard", Profiles.Count: >= 3 and <= 16 }
            || !new[] { "minimal", "standard", "developer" }.All(profiles.Profiles.ContainsKey)
            || profiles.Profiles.Values.Any(entries => !ValidEntries(entries)))
            throw new InvalidDataException("Invalid bundled application profiles.");
        return profiles;
    }

    private static bool ValidEntries(ProfileApplication[]? entries) => entries is { Length: <= 128 }
        && entries.All(entry => entry is not null && OfficialDesktopPackages.IsExternalOfficialId(entry.AppId)
            && entry.InstallPolicy is "offline" or "interactive"
            && RuntimeSemanticVersion.TryParse(entry.MinimumVersion, out _))
        && entries.Select(entry => entry.AppId).Distinct(StringComparer.Ordinal).Count() == entries.Length;

    internal static bool MeetsVersion(string version, string minimum) =>
        RuntimeSemanticVersion.TryParse(version, out var release)
        && RuntimeSemanticVersion.TryParse(minimum, out var floor) && release.CompareTo(floor) >= 0;

    private const string ProfileFlag = "--installation-profile=";
    private const string ExcludeFlag = "--exclude-bundled-app=";

    private static ProfileSelection SelectProfile(ProfileDocument document, ProfileSelection? saved, string[] options)
    {
        var requested = options.Where(option => option.StartsWith(ProfileFlag, StringComparison.Ordinal))
            .Select(option => option[ProfileFlag.Length..]).ToArray();
        var exclusions = options.Where(option => option.StartsWith(ExcludeFlag, StringComparison.Ordinal))
            .Select(option => option[ExcludeFlag.Length..]).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length > 1) throw new InvalidDataException("Choose one installation profile.");
        var profile = requested.SingleOrDefault() ?? saved?.Profile ?? document.DefaultProfile;
        if (profile is null || !document.Profiles.TryGetValue(profile, out var entries)
            || exclusions.Any(id => !entries.Any(entry => entry.AppId == id)))
            throw new InvalidDataException("Invalid installation profile selection.");
        if (saved is not null && saved.Profile == profile)
        {
            if (saved.Excluded is null) throw new InvalidDataException("Invalid saved installation profile.");
            if (exclusions.Length == 0 || saved.Excluded.SequenceEqual(exclusions, StringComparer.Ordinal))
                return saved;
        }
        if (saved is null || saved.Profile == profile)
            return new(profile, exclusions, saved?.OtherProfileExclusions);

        var other = saved.OtherProfileExclusions is null
            ? new Dictionary<string, string[]>(StringComparer.Ordinal)
            : new Dictionary<string, string[]>(saved.OtherProfileExclusions, StringComparer.Ordinal);
        if (saved.Profile is { Length: > 0 } && document.Profiles.ContainsKey(saved.Profile) && saved.Excluded is { Length: > 0 })
            other[saved.Profile] = saved.Excluded;
        if (other.Remove(profile, out var previous) && exclusions.Length == 0 && previous is not null)
            exclusions = previous;
        return new(profile, exclusions, other);
    }

    internal sealed record ProfileDocument(int SchemaVersion, string DefaultProfile, Dictionary<string, ProfileApplication[]> Profiles);
    internal sealed record ProfileApplication(string AppId, string MinimumVersion, string InstallPolicy);
    internal sealed record ProfileArchive(string DisplayName, string Version, bool CanProvisionOffline);
    internal sealed record ProfileSelection(string Profile, string[] Excluded,
        Dictionary<string, string[]>? OtherProfileExclusions = null);
    private sealed record ProvisionedApplication(string Id, string Version);
}
