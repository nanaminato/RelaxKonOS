using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Certificates;

public enum CertificateStatus { Pending, Validating, Issued, Active, Renewing, Failed, Expired, Revoked }
public enum CertificateChallengeType { DirectHttp01, WebRootHttp01, Dns01 }
public enum CertificateKeyAlgorithm { EcdsaP256, Rsa2048 }
public enum CertificateKind { Acme, SelfSigned }
public enum CertificateOperationState { Queued, Running, Succeeded, Failed, Cancelled }

public sealed record CertificateDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("primaryDomain")] string PrimaryDomain,
    [property: JsonPropertyName("subjectAlternativeNames")] IReadOnlyList<string> SubjectAlternativeNames,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("serialNumber")] string? SerialNumber,
    [property: JsonPropertyName("thumbprint")] string? Thumbprint,
    [property: JsonPropertyName("notBefore")] DateTimeOffset? NotBefore,
    [property: JsonPropertyName("notAfter")] DateTimeOffset? NotAfter,
    [property: JsonPropertyName("status")] CertificateStatus Status,
    [property: JsonPropertyName("challengeType")] CertificateChallengeType ChallengeType,
    [property: JsonPropertyName("keyAlgorithm")] CertificateKeyAlgorithm KeyAlgorithm,
    [property: JsonPropertyName("renewalWindowStart")] DateTimeOffset? RenewalWindowStart,
    [property: JsonPropertyName("renewalWindowEnd")] DateTimeOffset? RenewalWindowEnd,
    [property: JsonPropertyName("lastRenewalAt")] DateTimeOffset? LastRenewalAt,
    [property: JsonPropertyName("lastRenewalProblemCode")] string? LastRenewalProblemCode,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("kind")] CertificateKind Kind = CertificateKind.Acme,
    [property: JsonPropertyName("fingerprintSha256")] string? FingerprintSha256 = null);

public sealed record RequestCertificateRequest(
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("challengeType")] CertificateChallengeType ChallengeType,
    [property: JsonPropertyName("contactEmail")] string ContactEmail,
    [property: JsonPropertyName("acceptedTerms")] bool AcceptedTerms,
    [property: JsonPropertyName("keyAlgorithm")] CertificateKeyAlgorithm KeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256,
    [property: JsonPropertyName("publicReachabilityConfirmed")] bool PublicReachabilityConfirmed = false);

public sealed record CreateSelfSignedCertificateRequest(
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("keyAlgorithm")] CertificateKeyAlgorithm KeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256,
    [property: JsonPropertyName("validityDays")] int ValidityDays = 365);

public sealed record CertificatePreflightRequest(
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("challengeType")] CertificateChallengeType ChallengeType);

public sealed record DeleteCertificateRequest(
    [property: JsonPropertyName("confirmed")] bool Confirmed);

public sealed record RevokeCertificateRequest(
    [property: JsonPropertyName("confirmed")] bool Confirmed);

public sealed record CertificateDomainPreflightDto(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("ipv4Addresses")] IReadOnlyList<string> Ipv4Addresses,
    [property: JsonPropertyName("ipv6Addresses")] IReadOnlyList<string> Ipv6Addresses,
    [property: JsonPropertyName("problemCode")] string ProblemCode);

public sealed record CertificatePreflightResultDto(
    [property: JsonPropertyName("canProceed")] bool CanProceed,
    [property: JsonPropertyName("port80Available")] bool? Port80Available,
    [property: JsonPropertyName("requiresAdministrator")] bool RequiresAdministrator,
    [property: JsonPropertyName("domains")] IReadOnlyList<CertificateDomainPreflightDto> Domains,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("requiresPublicReachabilityConfirmation")] bool RequiresPublicReachabilityConfirmation = true);

public sealed record CertificateOperationDto(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("state")] CertificateOperationState State,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt);
