using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Git;

public sealed record GitTagDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha")] string Sha);
public sealed record GitTagCreateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("reference")] string Reference = "HEAD");
public sealed record GitReferenceRequest([property: JsonPropertyName("reference")] string Reference);
public sealed record GitStashDto(
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("message")] string Message);
/// <summary>save, apply, pop or drop. Existing entries are addressed by SHA and verified under the write lock.</summary>
public sealed record GitStashRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("sha")] string? Sha = null,
    [property: JsonPropertyName("message")] string? Message = null);
public sealed record GitWorktreeDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("isLocked")] bool IsLocked = false);
public sealed record GitWorktreeCreateRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reference")] string Reference = "HEAD",
    [property: JsonPropertyName("newBranch")] string? NewBranch = null);
public sealed record GitWorktreeRemoveRequest([property: JsonPropertyName("path")] string Path);
public sealed record GitPatchRequest(
    [property: JsonPropertyName("reference")] string? Reference = null,
    [property: JsonPropertyName("staged")] bool Staged = false);
/// <summary>Complete textual patch, refused instead of truncated when too large or binary.</summary>
public sealed record GitPatchDto([property: JsonPropertyName("patch")] string Patch);
public sealed record GitPatchApplyRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("stage")] bool Stage = false);
public sealed record GitCloneResult(
    [property: JsonPropertyName("result")] GitOperationResult Result,
    [property: JsonPropertyName("repository")] GitRepositoryDto? Repository = null);
public sealed record GitCloneRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("credentials")] GitCredentialRequest? Credentials = null);

/// <summary>Actual commit ancestry, including parents outside the currently filtered log.</summary>
public sealed record GitGraphCommitDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("parents")] IReadOnlyList<string> Parents,
    [property: JsonPropertyName("isHead"), JsonRequired] bool IsHead);
public sealed record GitConsoleEntryDto(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("exitCode")] int ExitCode);
