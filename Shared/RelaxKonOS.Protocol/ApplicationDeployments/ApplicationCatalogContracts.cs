using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ApplicationDeployments;

/// <summary>
/// The versioned, server-owned application-purpose catalogue.  Entries describe a deliberately
/// small deployment definition; they are not executable scripts and clients must not interpret
/// arbitrary UI or shell content from them.
/// </summary>
public sealed record ApplicationCatalogTemplateDto(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("publisher")] string Publisher,
    [property: JsonPropertyName("source")] string Source,
    /// <summary>True only after the server has verified the configured catalogue source.</summary>
    [property: JsonPropertyName("trusted")] bool Trusted,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("supportedPlatforms")] IReadOnlyList<string> SupportedPlatforms,
    [property: JsonPropertyName("requiredCapabilities")] IReadOnlyList<string> RequiredCapabilities,
    [property: JsonPropertyName("minimumResources")] ApplicationResourceLimitsDto MinimumResources,
    [property: JsonPropertyName("fields")] IReadOnlyList<ApplicationCatalogFieldDto> Fields,
    [property: JsonPropertyName("volumes")] IReadOnlyList<ApplicationVolumeDto> Volumes,
    [property: JsonPropertyName("containerPort")] int ContainerPort,
    [property: JsonPropertyName("accessPath")] string? AccessPath,
    [property: JsonPropertyName("maintenanceNotes")] string MaintenanceNotes,
    [property: JsonPropertyName("withdrawn")] bool Withdrawn = false);

/// <summary>A constrained native form field.  The only supported types are text, number, enum and secret.</summary>
public sealed record ApplicationCatalogFieldDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("defaultValue")] string? DefaultValue,
    [property: JsonPropertyName("options")] IReadOnlyList<string> Options,
    [property: JsonPropertyName("labels")] ApplicationCatalogTextDto Labels,
    [property: JsonPropertyName("help")] string? Help = null);

/// <summary>Product text carried as data so a client can render a server-owned form in its supported languages.</summary>
public sealed record ApplicationCatalogTextDto(
    [property: JsonPropertyName("en")] string English,
    [property: JsonPropertyName("zh")] string Chinese,
    [property: JsonPropertyName("ja")] string Japanese);

/// <summary>One supplied field value. Secret values are accepted only to create the protected deployment secret.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplicationCatalogFieldValueDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] string Value);

/// <summary>Creates a definition from the exact catalogue version selected by the operator, then queues deployment.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallCatalogApplicationRequest(
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("templateVersion")] string TemplateVersion,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonRequired, JsonPropertyName("hostPort")] int HostPort,
    [property: JsonPropertyName("fields")] IReadOnlyList<ApplicationCatalogFieldValueDto> Fields,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);

/// <summary>Returned after a catalogue install is durably queued. The version is bound to this instance.</summary>
public sealed record CatalogApplicationInstallDto(
    [property: JsonPropertyName("application")] ApplicationDto Application,
    [property: JsonPropertyName("operation")] DeploymentOperationDto Operation,
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("templateVersion")] string TemplateVersion);
