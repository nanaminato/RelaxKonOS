using System.Security.Cryptography;
using System.Text;

namespace RelaxKonOS.Server.Docker;

/// <summary>Domain failure carrying a stable problem code. Status codes follow the existing REST convention.</summary>
internal sealed class DockerStackException(string problemCode, int statusCode = 409) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Stable problem codes of the Compose stack domain. They are part of the wire contract: a client
/// explains a refusal from the code alone, so the spelling must not drift between releases.
/// </summary>
internal static class DockerStackProblem
{
    public const string InvalidName = "docker.stack_invalid_name";
    public const string InvalidCompose = "docker.stack_invalid_compose";
    public const string FeatureUnsupported = "docker.compose_feature_unsupported";
    public const string VariableUnresolved = "docker.compose_variable_unresolved";
    public const string DefinitionChanged = "docker.stack_definition_changed";
    public const string ConfirmationRequired = "docker.confirmation_required";
    public const string NotFound = "docker.stack_not_found";
    public const string NoServices = "docker.stack_no_services";
    public const string PartialFailure = "docker.stack_partial_failure";
    public const string Interrupted = "docker.stack_interrupted";
    public const string Cancelled = "docker.stack_cancelled";
    public const string ComposeFailed = "docker.compose_failed";
    public const string OperationNotFound = "docker.stack_operation_not_found";
    public const string OperationConflict = "docker.stack_operation_conflict";
    public const string IdempotencyRequired = "docker.stack_idempotency_required";
    public const string IdempotencyConflict = "docker.stack_idempotency_conflict";
    public const string NotCancellable = "docker.stack_not_cancellable";
    public const string StoreUnavailable = "docker.stack_store_unavailable";
    public const string EngineUnavailable = "docker.unavailable";
    public const string ValidationFailed = "docker.validation_failed";
}

/// <summary>Shared value rules for the Compose stack domain.</summary>
internal static class DockerStackValidation
{
    /// <summary>A Compose project name is also the Docker resource prefix, so it stays narrow.</summary>
    public static bool IsValidProjectName(string? value) => value is { Length: >= 1 and <= 63 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>Opaque, stable reference used for the ledger. Nothing reversible is ever stored.</summary>
    public static string Reference(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// The identity of the exact source a preview was taken from. A deployment carries it back so an
    /// approval given for one document can never be applied to a different one.
    /// </summary>
    public static string DefinitionVersion(string name, string composeYaml) => Reference(name + "\n" + composeYaml);

    public static bool IsValidProblemCode(string? code) => code is null
        || code.Length <= 120 && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    /// <summary>Every ledger reference is a 64-character uppercase SHA-256 digest. A record that does
    /// not match is corrupt, not merely unknown.</summary>
    public static bool IsValidReference(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigitUpper);
}
