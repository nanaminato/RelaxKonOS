using System.Text.Json;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Server.Installations;

public sealed class InstallationException(string problemCode, int statusCode = 409) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The root transaction stopped at its own cancellation boundary and left nothing behind. It is a distinct
/// type so the ledger records Cancelled rather than Failed: "the operator stopped it" is not a failure.
/// </summary>
public sealed class InstallationCancelledException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}

/// <summary>The transaction was interrupted and reconciled; this is distinct from user cancellation or failure.</summary>
public sealed class InstallationInterruptedException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}

public sealed record InstallationProgress(InstallationStage Stage, int? Progress = null, bool Cancellable = false);
public interface IInstallationProgress
{
    Guid OperationId { get; }
    Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default);
}
public sealed record InstallationRecovery(InstallationOperationState State, string? ProblemCode = null);

/// <summary>Domain boundary. Implementations call fixed installers; the coordinator never executes host commands.</summary>
public interface IInstallationService
{
    InstallationServiceId Id { get; }
    IReadOnlyList<string> Resources { get; }
    object Validate(InstallationOperationKind kind, JsonElement options, string idempotencyKey);
    Task StartAsync(InstallationOperationKind kind, object options, string actor, IInstallationProgress progress, CancellationToken ct);
    Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct);
    bool Cancel(InstallationOperationDto operation);

    /// <summary>
    /// True when <see cref="Cancel"/> only records a request for the domain. The worker then decides the
    /// outcome itself; the coordinator must not cancel its own wait, which would publish a cancellation the
    /// root transaction never performed.
    /// </summary>
    bool CooperativeCancel { get; }

    /// <summary>Delivers the recorded request to the domain. It is a no-op for aborting services.</summary>
    Task RequestCancelAsync(InstallationOperationDto operation, CancellationToken ct);
}
