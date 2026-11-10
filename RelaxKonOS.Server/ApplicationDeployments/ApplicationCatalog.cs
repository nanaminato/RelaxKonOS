using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>Release data shipped alongside the Server. The directory has the same trust boundary as its binaries.</summary>
internal static class ApplicationCatalog
{
    public const string SchemaVersion = "2";
    private static readonly Lazy<Entry[]> Official = new(() => Load(Path.Combine(AppContext.BaseDirectory, "Assets", "ApplicationCatalog")));
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 12,
    };

    public static ApplicationCatalogTemplateDto[] DescribeAll() => [.. Official.Value.Select(entry => entry.Describe())];

    public static Entry Require(string? id, string? version)
    {
        return Official.Value.FirstOrDefault(x => x.Id == id && x.Version == version)
            ?? throw (Official.Value.Any(x => x.Id == id)
                ? new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.CatalogTemplateVersionUnavailable, 409)
                : new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.CatalogTemplateNotFound, 404));
    }

    internal static Entry[] Load(string directory)
    {
        var entries = new List<Entry>();
        var identities = new HashSet<(string Id, string Version)>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            try
            {
                if (stream.Length > 64 * 1024 || JsonSerializer.Deserialize<Manifest>(stream, ManifestJson) is not { Release: { } entry } manifest
                    || manifest.SchemaVersion != SchemaVersion || !entry.IsValid()
                    || !identities.Add((entry.Id, entry.Version)))
                    throw new JsonException("Invalid or duplicate official application release.");
                entries.Add(entry);
            }
            catch (JsonException error)
            {
                throw new InvalidDataException($"Invalid official application manifest: {Path.GetFileName(file)}.", error);
            }
        }
        return [.. entries];
    }

    private sealed record Manifest(string SchemaVersion, Entry Release);

    internal sealed record Entry(string Id, string Version, string Purpose, string Description, string ImageReference, int ContainerPort,
        string HealthPath, ApplicationVolumeDto[] Volumes, Field[] Fields,
        string UpdateNotes = "The workload is stopped during the upgrade. The instance keeps its configuration, secret versions and managed data volumes.",
        UpgradeSource[]? UpgradeFrom = null)
    {
        internal bool IsValid() => ApplicationDeploymentValidation.IsValidName(Id)
            && Version is { Length: <= 40 } && System.Version.TryParse(Version, out _)
            && IsText(Purpose, 120) && IsText(Description, 1024) && IsText(UpdateNotes, 1024)
            && DockerImageReference.HasSha256Digest(ImageReference)
            && ApplicationDeploymentValidation.IsValidPort(ContainerPort)
            && HealthPath is { Length: > 0 } && !HealthPath.StartsWith("//", StringComparison.Ordinal)
            && !HealthPath.Contains('\\') && ApplicationDeploymentValidation.IsValidHealthPath(HealthPath)
            && Volumes is { Length: <= 8 }
            && Volumes.All(volume => volume is not null && ApplicationDeploymentValidation.IsValidVolumeName(volume.Name)
                && ApplicationDeploymentValidation.IsValidContainerPath(volume.ContainerPath)
                && volume.ContainerPath != ApplicationDeploymentService.SecretsMountPath)
            && Volumes.Select(volume => volume.Name).Distinct(StringComparer.Ordinal).Count() == Volumes.Length
            && Volumes.Select(volume => volume.ContainerPath).Distinct(StringComparer.Ordinal).Count() == Volumes.Length
            && Fields is { Length: <= 64 } && Fields.All(field => field is not null && field.IsValid())
            && Fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() == Fields.Length
            && Fields.Select(field => field.EnvironmentName).Distinct(StringComparer.Ordinal).Count() == Fields.Length
            && (UpgradeFrom is null || UpgradeFrom.Length <= 16
                && UpgradeFrom.All(source => source is not null && source.Version is { Length: <= 40 }
                    && System.Version.TryParse(source.Version, out var sourceVersion) && sourceVersion < System.Version.Parse(Version)
                    && DockerImageReference.HasSha256Digest(source.ImageReference)
                    && !source.ImageReference.AsSpan()[^64..].SequenceEqual(ImageReference.AsSpan()[^64..]))
                && UpgradeFrom.Select(source => source.Version).Distinct(StringComparer.Ordinal).Count() == UpgradeFrom.Length);

        public ApplicationCatalogTemplateDto Describe() => new(SchemaVersion, Id, Version, "RelaxKonOS", "built-in", true, Purpose, Description,
            ["linux/amd64"], ["server.application-deployments", "server.docker"],
            new(1, 512L * 1024 * 1024, 512), [.. Fields.Select(x => x.Describe())], Volumes, ContainerPort, HealthPath,
            "Updates and removal retain managed data volumes. Installed instances are never changed by a catalogue refresh.");

        public (CreateApplicationRequest Definition, DeploymentSourceInputDto Source) Bind(InstallCatalogApplicationRequest request)
        {
            var name = request.Name?.Trim();
            if (!ApplicationDeploymentValidation.IsValidName(name) || request.HostPort is < 1 or > 65535)
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.CatalogFieldInvalid, 400);
            var supplied = request.Fields ?? [];
            if (supplied.Any(x => x is null)
                || supplied.Count != supplied.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count()
                || supplied.Any(x => !Fields.Any(field => field.Id == x.Id)))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.CatalogFieldInvalid, 400);
            var configuration = new List<ApplicationConfigEntryDto>();
            foreach (var field in Fields)
            {
                var value = supplied.FirstOrDefault(x => x.Id == field.Id)?.Value ?? field.DefaultValue;
                if (field.Required && string.IsNullOrWhiteSpace(value) || value is not null && !field.Accepts(value))
                    throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.CatalogFieldInvalid, 400);
                if (value is not null)
                    configuration.Add(new(field.EnvironmentName, value, field.Type == "secret"));
            }
            return (new(name!, ApplicationSourceKind.Image, ApplicationWorkloadKind.Web, ApplicationReadinessLevel.Http,
                HealthPath, ContainerPort, request.HostPort, "127.0.0.1", Describe().MinimumResources, Volumes, configuration),
                new(ImageReference: ImageReference));
        }
    }

    internal sealed record UpgradeSource(string Version, string ImageReference);

    private static bool IsText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    internal sealed record Field(string Id, string Type, bool Required, string EnvironmentName, ApplicationCatalogTextDto Labels,
        string? DefaultValue = null)
    {
        public ApplicationCatalogFieldDto Describe() => new(Id, Type, Required, DefaultValue, [], Labels);
        internal bool IsValid() => ApplicationDeploymentValidation.IsValidEnvironmentName(Id)
            && ApplicationDeploymentValidation.IsValidEnvironmentName(EnvironmentName) && Type is "text" or "secret"
            && (DefaultValue is null || Type == "text" && Accepts(DefaultValue))
            && Labels is not null && IsText(Labels.English, 120) && IsText(Labels.Chinese, 120) && IsText(Labels.Japanese, 120);
        public bool Accepts(string value) => Type is "text" or "secret" && value.Length is <= 4096 && !value.Any(char.IsControl);
    }
}
