using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.UserExecution;

/// <summary>Protocol constants for the dedicated, local-only user-execution channel.</summary>
public static class UserExecutionProtocol
{
    public const string Version = "1.0";
    public const int MaximumRequestBytes = 16 * 1024 * 1024;
    public const int MaximumFileContentBytes = 12 * 1024 * 1024;
}

/// <summary>
/// Closed operations allowed on the user-execution channel. This is deliberately not a process
/// runner: executable paths, arguments, shells, passwords, tokens and environment variables do
/// not belong in this contract.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UserExecutionOperationKind>))]
public enum UserExecutionOperationKind
{
    FileListDirectory,
    FileGetSpecialLocations,
    FileGetInfo,
    FileRead,
    FileWrite,
    FileDelete,
    FileRename,
    FileMove,
    FileCopy,
    FileUpload,
    FileCreateDirectory,
    FileGetProperties,
    FileSetUnixPermissions,
    GitExecute,
    TerminalStart,
}

/// <summary>
/// A server-derived stable OS identity reference. It is never an HTTP request field. Linux uses
/// the canonical NSS UID and Windows uses a canonical SID.
/// </summary>
public sealed record UserExecutionIdentity(
    [property: JsonPropertyName("platform")] PlatformKind Platform,
    [property: JsonPropertyName("stableIdentity")] string StableIdentity,
    [property: JsonPropertyName("canonicalAccount")] string CanonicalAccount,
    [property: JsonPropertyName("homeDirectory")] string HomeDirectory);

/// <summary>Structured local Helper request. The Server creates every field after JWT validation.</summary>
public sealed record UserExecutionRequest(
    [property: JsonPropertyName("identity")] UserExecutionIdentity Identity,
    [property: JsonPropertyName("operation")] UserExecutionOperationKind Operation,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("destinationPath")] string? DestinationPath = null,
    [property: JsonPropertyName("newName")] string? NewName = null,
    [property: JsonPropertyName("fileName")] string? FileName = null,
    [property: JsonPropertyName("overwrite")] bool Overwrite = false,
    [property: JsonPropertyName("contentBase64")] string? ContentBase64 = null,
    [property: JsonPropertyName("unixMode")] int? UnixMode = null,
    [property: JsonPropertyName("gitArguments")] IReadOnlyList<string>? GitArguments = null,
    [property: JsonPropertyName("terminalShell")] string? TerminalShell = null,
    [property: JsonPropertyName("operationId")] Guid? OperationId = null,
    [property: JsonPropertyName("version")] string Version = UserExecutionProtocol.Version);

[JsonConverter(typeof(JsonStringEnumConverter<UserExecutionProblemCode>))]
public enum UserExecutionProblemCode
{
    None,
    AuthenticationInvalid,
    IdentityUnavailable,
    IdentityMismatch,
    IdentityNotExecutable,
    UnsupportedPlatform,
    HelperUnavailable,
    InvalidProtocol,
    InvalidRequest,
    AccessDenied,
    NotFound,
    Conflict,
    ContentTooLarge,
    TimedOut,
    InternalError,
}

/// <summary>Non-secret, versioned result for the dedicated local user-execution channel.</summary>
public sealed record UserExecutionResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("outputBase64")] string? OutputBase64 = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("problemCode")] UserExecutionProblemCode ProblemCode = UserExecutionProblemCode.None,
    [property: JsonPropertyName("version")] string Version = UserExecutionProtocol.Version);
