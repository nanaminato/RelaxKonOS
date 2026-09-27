using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>Trusted built-in application-purpose catalogue. Entries select only constrained AD02 input.</summary>
internal static class ApplicationCatalog
{
    public const string SchemaVersion = "1";
    private static readonly Entry[] Entries =
    [
        Entry.Web("personal-site", "1.0.0", "Personal website", "A small private website served by Nginx.", "nginx:1.27.3-alpine", 80, "/usr/share/nginx/html", "content"),
        Entry.Web("status-monitor", "1.0.0", "Status monitor", "Uptime Kuma with persistent monitoring data.", "louislam/uptime-kuma:1.23.16", 3001, "/app/data", "data"),
        Entry.Web("file-service", "1.0.0", "File service", "File Browser with a managed persistent database.", "filebrowser/filebrowser:v2.31.2", 80, "/database", "database", "FB_PASSWORD"),
        Entry.Web("webhook-service", "1.0.0", "Webhook service", "A managed webhook listener.", "tarampampam/webhook-tester:2.8.2", 8080, null, null),
    ];

    public static ApplicationCatalogTemplateDto[] DescribeAll() => [.. Entries.Select(entry => entry.Describe())];
    public static ApplicationCatalogTemplateDto? Describe(string id) => Entries.FirstOrDefault(x => x.Id == id)?.Describe();

    public static Entry Require(string? id, string? version)
    {
        var entry = Entries.FirstOrDefault(x => x.Id == id);
        if (entry is null) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.CatalogTemplateNotFound, 404);
        if (entry.Version != version) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.CatalogTemplateVersionUnavailable, 409);
        return entry;
    }

    internal sealed record Entry(string Id, string Version, string Purpose, string Description, string ImageReference, int ContainerPort,
        ApplicationVolumeDto[] Volumes, Field[] Fields)
    {
        public static Entry Web(string id, string version, string purpose, string description, string image, int port,
            string? volumePath, string? volumeName, string? secret = null) => new(id, version, purpose, description, image, port,
            volumePath is null ? [] : [new(volumeName!, volumePath)],
            secret is null ? [] : [new("adminPassword", "secret", true, null, secret,
                new("Administrator password", "管理员密码", "管理者パスワード"))]);

        public ApplicationCatalogTemplateDto Describe() => new(SchemaVersion, Id, Version, "RelaxKonOS", "built-in", true, Purpose, Description,
            ApplicationDeploymentValidation.SupportedPlatforms, ["server.application-deployments", "server.docker"],
            new(1, 512L * 1024 * 1024, 512), [.. Fields.Select(x => x.Describe())], Volumes, ContainerPort, "/",
            "Updates and removal retain managed data volumes. Installed instances are never changed by a catalogue refresh.");

        public (CreateApplicationRequest Definition, DeploymentSourceInputDto Source) Bind(InstallCatalogApplicationRequest request)
        {
            var name = request.Name?.Trim();
            if (!ApplicationDeploymentValidation.IsValidName(name))
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
                if (value is not null && field.EnvironmentName is not null)
                    configuration.Add(new(field.EnvironmentName, value, field.Type == "secret"));
            }
            return (new(name!, ApplicationSourceKind.Image, ApplicationWorkloadKind.Web, ApplicationReadinessLevel.Http,
                "/", ContainerPort, null, "127.0.0.1", new(1, 512L * 1024 * 1024, 512), Volumes, configuration),
                new(ImageReference: ImageReference));
        }
    }

    internal sealed record Field(string Id, string Type, bool Required, string? DefaultValue, string? EnvironmentName, ApplicationCatalogTextDto Labels)
    {
        public ApplicationCatalogFieldDto Describe() => new(Id, Type, Required, DefaultValue, [], Labels);
        public bool Accepts(string value) => Type is "text" or "secret" && value.Length is <= 4096 && !value.Any(char.IsControl);
    }
}
