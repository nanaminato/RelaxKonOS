using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.TaskOrchestration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VerifyRuntimeInstallationInput(
    [property: JsonRequired, JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonRequired, JsonPropertyName("expectedStateRevision")] long ExpectedStateRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VerifyRuntimeCatalogInput(
    [property: JsonRequired, JsonPropertyName("sourceId")] string SourceId,
    [property: JsonRequired, JsonPropertyName("expectedCatalogRevision")] long ExpectedCatalogRevision,
    [property: JsonRequired, JsonPropertyName("expectedTrustRevision")] long ExpectedTrustRevision);
