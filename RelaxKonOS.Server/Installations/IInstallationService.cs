using System.Text.Json;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Server.Installations;

public sealed class InstallationException(string problemCode, int statusCode = 409) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public int StatusCode { get; } = statusCode;
}

public sealed record InstallationProgress(InstallationStage Stage, int? Progress = null, bool Cancellable = false);
public interface IInstallationProgress
{
    Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default);
}
public sealed record InstallationRecovery(InstallationOperationState State, string? ProblemCode = null);

/// <summary>Domain boundary. Implementations call fixed installers; the coordinator never executes host commands.</summary>
public interface IInstallationService
{
    InstallationServiceId Id { get; }
    IReadOnlyList<string> Resources { get; }
    object Validate(InstallationOperationKind kind, JsonElement options);
    Task StartAsync(InstallationOperationKind kind, object options, string actor, IInstallationProgress progress, CancellationToken ct);
    Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct);
    bool Cancel(InstallationOperationDto operation);
}
