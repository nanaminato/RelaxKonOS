namespace RelaxKonOS.Protocol.ProcessGuardian;

/// <summary>One shot, shell free execution. The Server supplies OwnerIdentity and RunAsIdentity.</summary>
public sealed record ScriptTaskDefinitionDto(
    string Id,
    string OwnerIdentity,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int TimeoutSeconds,
    string RunAs,
    string RunAsIdentity);

/// <summary>Public request. An administrator password is used only during this submission.</summary>
public sealed record SubmitScriptTaskRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int TimeoutSeconds,
    string? RunAs,
    RunAsAdministratorApproval? RunAsApproval = null);

public sealed record ScriptOutputLineDto(DateTimeOffset Timestamp, string Stream, string Text);

/// <summary>Persisted outcome; a running task found after Agent restart becomes Interrupted.</summary>
public sealed record ScriptTaskDto(
    string Id,
    string OwnerIdentity,
    string ExecutablePath,
    string RunAs,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    int? ExitCode,
    string? ProblemCode,
    IReadOnlyList<ScriptOutputLineDto> Output,
    bool OutputTruncated);
