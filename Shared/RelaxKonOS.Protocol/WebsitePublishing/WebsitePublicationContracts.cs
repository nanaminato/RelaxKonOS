using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.WebsitePublishing;

[JsonConverter(typeof(JsonStringEnumConverter<WebsitePublicationState>))]
public enum WebsitePublicationState { Queued, Running, Succeeded, PartialFailed, Failed, Cancelled, Interrupted }

[JsonConverter(typeof(JsonStringEnumConverter<WebsitePublicationStage>))]
public enum WebsitePublicationStage { Queued, Preflight, Certificate, Configuring, Associating, Verifying, Completed, Failed, Interrupted }

[JsonConverter(typeof(JsonStringEnumConverter<WebsitePublicationCheckState>))]
public enum WebsitePublicationCheckState { Passed, Failed, Unverified }

/// <summary>One server-observed fact. It never asserts that a different network observed the same result.</summary>
public sealed record WebsitePublicationCheckDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("observer")] string Observer,
    [property: JsonPropertyName("state")] WebsitePublicationCheckState State,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt);

/// <summary>
/// A confirmed request to expose one running application through a RelaxKonOS-owned Nginx site.
/// When <see cref="CertificateId"/> is null, the server issues an ACME HTTP-01 certificate using
/// the supplied contact and confirmation. DNS credentials are deliberately not a part of this contract.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishWebsiteRequest(
    [property: JsonPropertyName("applicationId")] Guid ApplicationId,
    [property: JsonPropertyName("webServerId")] string WebServerId,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId = null,
    [property: JsonPropertyName("contactEmail")] string? ContactEmail = null,
    [property: JsonPropertyName("acceptedTerms")] bool AcceptedTerms = false,
    [property: JsonPropertyName("publicReachabilityConfirmed")] bool PublicReachabilityConfirmed = false,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);

/// <summary>Durable, secret-free progress record for a website publication.</summary>
public sealed record WebsitePublicationOperationDto(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("applicationId")] Guid ApplicationId,
    [property: JsonPropertyName("webServerId")] string WebServerId,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("siteId")] string? SiteId,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId,
    [property: JsonPropertyName("certificateOperationId")] Guid? CertificateOperationId,
    [property: JsonPropertyName("state")] WebsitePublicationState State,
    [property: JsonPropertyName("stage")] WebsitePublicationStage Stage,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("recoveryProblemCode")] string? RecoveryProblemCode,
    [property: JsonPropertyName("checks")] IReadOnlyList<WebsitePublicationCheckDto> Checks,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt);

public static class WebsitePublicationApiRoutes
{
    private const string V1 = RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Root = $"/{V1}/website-publications";
    public const string Publish = Root;
    public const string CollectionPattern = "";
    public const string ByApplication = $"{Root}/applications/{{applicationId}}";
    public const string ByApplicationPattern = "/applications/{applicationId:guid}";
    public const string Operation = $"{Root}/operations/{{operationId}}";
    public const string OperationPattern = "/operations/{operationId:guid}";
}
