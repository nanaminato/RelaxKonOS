using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Git;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GitBuildCredentialRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("token")] string Token);

public sealed record GitBuildCredentialDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GitBuildResolveRequest(
    [property: JsonPropertyName("repositoryUrl")] string RepositoryUrl,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("credentialId")] Guid? CredentialId = null);

public sealed record GitBuildResolvedDto(
    [property: JsonPropertyName("repositoryUrl")] string RepositoryUrl,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("commitSha")] string CommitSha);

public sealed record GitBuildRefDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("commitSha")] string CommitSha);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GitBuildRequest(
    [property: JsonPropertyName("repositoryUrl")] string RepositoryUrl,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("commitSha")] string CommitSha,
    [property: JsonPropertyName("contextDirectory")] string ContextDirectory,
    [property: JsonPropertyName("dockerfile")] string Dockerfile,
    [property: JsonPropertyName("credentialId")] Guid? CredentialId = null);

public sealed record GitBuildOperationDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("repositoryUrl")] string RepositoryUrl,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("commitSha")] string CommitSha,
    [property: JsonPropertyName("contextDirectory")] string ContextDirectory,
    [property: JsonPropertyName("dockerfile")] string Dockerfile,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("problemCode")] string? ProblemCode,
    [property: JsonPropertyName("imageReference")] string? ImageReference,
    [property: JsonPropertyName("imageId")] string? ImageId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("logs")] IReadOnlyList<string> Logs);
