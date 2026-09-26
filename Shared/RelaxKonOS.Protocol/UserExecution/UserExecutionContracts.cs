using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Protocol.UserExecution;

/// <summary>Protocol constants for the dedicated, local-only user-execution channel.</summary>
public static class UserExecutionProtocol
{
    public const string Version = "1.2";
    // A 12 MiB payload expands to 16 MiB in base64; leave bounded room for the JSON envelope.
    public const int MaximumRequestBytes = 17 * 1024 * 1024;
    public const int MaximumFileContentBytes = 12 * 1024 * 1024;
    public const int MaximumResultBytes = 17 * 1024 * 1024;
    public const int MaximumResponseBytes = 24 * 1024 * 1024;
    // Windows adds an authenticated base64 envelope around the already bounded response.
    public const int MaximumAuthenticatedPipeFrameBytes = 36 * 1024 * 1024;
    public const int MaximumTerminalInputBytes = 1024 * 1024;
    public const string WindowsPipeSuffix = "-user";

    public static bool IsEligibleLinuxUserId(uint uid) => uid >= 1000 && uid != 65534;

    /// <summary>
    /// A home directory is judged by the rules of the platform that owns the identity, never by the
    /// host's: a POSIX home is absolute only when it starts with <c>/</c>, which Win32 path rules
    /// reject, and a drive-qualified Windows profile is not absolute under POSIX rules. Judging a
    /// foreign platform's path with host rules would make eligibility depend on where the Server runs.
    /// </summary>
    public static bool IsEligibleHomeDirectory(HostPlatformKind platform, string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory) || homeDirectory.Contains('\0')) return false;
        return platform switch
        {
            HostPlatformKind.Linux => homeDirectory[0] == '/',
            HostPlatformKind.Windows => Path.IsPathFullyQualified(homeDirectory),
            _ => false,
        };
    }

    public static string WindowsPipeName(string privilegedPipeName) => privilegedPipeName + WindowsPipeSuffix;
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
    // Resumable upload staging. A staging file lives in its destination directory, so committing it is
    // a same-directory rename rather than a cross-volume copy, and it is created under the same
    // permission context as the file it will become.
    FileCreateStaging,
    FileAppendStaging,
    FileGetStagingLength,
    FileDeleteStaging,
    FileCommitStaging,
    GitExecute,
    TerminalStart,
}

/// <summary>
/// A server-derived stable OS identity reference. It is never an HTTP request field. Linux uses
/// the canonical NSS UID and Windows uses a canonical SID.
/// </summary>
public sealed record UserExecutionIdentity(
    [property: JsonPropertyName("platform")] HostPlatformKind Platform,
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
    // Confirmed length of the staging file a chunk is appended at. It is never an offset the caller may
    // pick: the server derives it from the session index and the Helper verifies the file really is that
    // long before writing.
    [property: JsonPropertyName("offset")] long? Offset = null,
    // Exact number of bytes the chunk must contribute. A shortfall is discarded rather than partially
    // kept, so the returned length stays the only offset a caller may treat as confirmed.
    [property: JsonPropertyName("expectedBytes")] long? ExpectedBytes = null,
    [property: JsonPropertyName("gitArguments")] IReadOnlyList<string>? GitArguments = null,
    [property: JsonPropertyName("terminalShell")] string? TerminalShell = null,
    [property: JsonPropertyName("terminalColumns")] int? TerminalColumns = null,
    [property: JsonPropertyName("terminalRows")] int? TerminalRows = null,
    [property: JsonPropertyName("terminalWidthPixels")] int? TerminalWidthPixels = null,
    [property: JsonPropertyName("terminalHeightPixels")] int? TerminalHeightPixels = null,
    [property: JsonPropertyName("operationId")] Guid? OperationId = null,
    [property: JsonPropertyName("correlation")] CorrelationContext? Correlation = null,
    [property: JsonPropertyName("version")] string Version = UserExecutionProtocol.Version);

[JsonConverter(typeof(JsonStringEnumConverter<UserExecutionProblemCode>))]
public enum UserExecutionProblemCode
{
    None,
    AuthenticationInvalid,
    IdentityUnavailable,
    IdentityMismatch,
    /// <summary>The addressed identity is not one this Server may execute ordinary operations as
    /// (root, a reserved/system account, nobody, or an account without a usable home directory).</summary>
    IdentityNotEligible,
    /// <summary>The identity is eligible, but the execution boundary refused to run as it: the
    /// deployment has no channel to become that account (missing or mismatched Helper).</summary>
    IdentityNotExecutable,
    UnsupportedPlatform,
    HelperUnavailable,
    InvalidProtocol,
    InvalidRequest,
    AccessDenied,
    NotFound,
    Conflict,
    ContentTooLarge,
    Cancelled,
    TimedOut,
    InternalError,
}

/// <summary>
/// Stable, kebab-case ProblemDetails type suffixes for the user-execution boundary. A client localizes
/// from the suffix, so each code keeps exactly one name, and each name keeps exactly one meaning.
/// </summary>
public static class UserExecutionProblemTypes
{
    public const string Base = "https://relaxkonos.app/problems/";
    public const string IdentityNotEligible = "identity-not-eligible";
    public const string IdentityNotExecutable = "identity-not-executable";
    public const string HelperUnavailable = "user-execution-helper-unavailable";
    public const string Unavailable = "user-execution-unavailable";

    public static string Suffix(UserExecutionProblemCode code) => code switch
    {
        UserExecutionProblemCode.IdentityNotEligible => IdentityNotEligible,
        UserExecutionProblemCode.IdentityNotExecutable => IdentityNotExecutable,
        UserExecutionProblemCode.HelperUnavailable => HelperUnavailable,
        _ => Unavailable,
    };

    /// <summary>Full type URI for a code, as every endpoint that reports the boundary must write it.</summary>
    public static string Uri(UserExecutionProblemCode code) => Base + Suffix(code);
}

/// <summary>Non-secret, versioned result for the dedicated local user-execution channel.</summary>
public sealed record UserExecutionResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("outputBase64")] string? OutputBase64 = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("problemCode")] UserExecutionProblemCode ProblemCode = UserExecutionProblemCode.None,
    [property: JsonPropertyName("version")] string Version = UserExecutionProtocol.Version);
