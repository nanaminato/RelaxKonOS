using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Identity;

public sealed record AliasConfigurationDto(
    [property: JsonPropertyName("canonicalUserId")] Guid CanonicalUserId,
    [property: JsonPropertyName("systemUsername")] string SystemUsername,
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("systemLoginEnabled")] bool SystemLoginEnabled,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("passwordChangedAt")] DateTimeOffset? PasswordChangedAt,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("unavailableReason")] string? UnavailableReason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AliasReauthentication(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("password")] string Password);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAliasRequest(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("aliasPassword")] string AliasPassword,
    [property: JsonPropertyName("currentSystemPassword")] string CurrentSystemPassword,
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameAliasRequest(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("reauthentication")] AliasReauthentication Reauthentication,
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeAliasPasswordRequest(
    [property: JsonPropertyName("newPassword")] string NewPassword,
    [property: JsonPropertyName("reauthentication")] AliasReauthentication Reauthentication,
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteAliasRequest(
    [property: JsonPropertyName("currentSystemPassword")] string CurrentSystemPassword,
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetSystemLoginRequest(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision);
