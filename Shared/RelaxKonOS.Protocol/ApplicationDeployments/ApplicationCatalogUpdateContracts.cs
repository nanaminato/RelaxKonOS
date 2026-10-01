using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ApplicationDeployments;

/// <summary>A read-only diff against the exact current definition and running revision. No secret values.</summary>
public sealed record CatalogApplicationUpdatePreviewDto(
    [property: JsonPropertyName("applicationId")] Guid ApplicationId,
    [property: JsonPropertyName("expectedUpdatedAt")] DateTimeOffset ExpectedUpdatedAt,
    [property: JsonPropertyName("expectedRevisionId")] Guid? ExpectedRevisionId,
    [property: JsonPropertyName("currentTemplateVersion")] string CurrentTemplateVersion,
    [property: JsonPropertyName("target")] ApplicationCatalogTemplateDto Target,
    [property: JsonPropertyName("currentImageReference")] string? CurrentImageReference,
    [property: JsonPropertyName("targetImageReference")] string TargetImageReference,
    [property: JsonPropertyName("updateNotes")] string UpdateNotes,
    [property: JsonPropertyName("blockers")] IReadOnlyList<string> Blockers);

/// <summary>Explicit update to one exact trusted template version. Existing definition and data are retained.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateCatalogApplicationRequest(
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("templateVersion")] string TemplateVersion,
    [property: JsonRequired, JsonPropertyName("expectedUpdatedAt")] DateTimeOffset ExpectedUpdatedAt,
    [property: JsonRequired, JsonPropertyName("expectedRevisionId")] Guid? ExpectedRevisionId,
    [property: JsonPropertyName("currentTemplateVersion")] string CurrentTemplateVersion,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);
