using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Git;

/// <summary>Public metadata only. Tokens never leave the server credential store.</summary>
public sealed record GitCredentialAccountDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("isDefault")] bool IsDefault);

/// <summary>HTTPS host (optionally with port), username and token. A null token updates metadata only.</summary>
public sealed record GitCredentialAccountRequest(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string? Password = null,
    [property: JsonPropertyName("isDefault")] bool IsDefault = true);

public sealed record GitFetchRequest(
    [property: JsonPropertyName("remote")] string? Remote = null,
    [property: JsonPropertyName("credentials")] GitCredentialRequest? Credentials = null);
