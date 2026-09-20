using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Protocol.Hubs;

public static class ApplicationDeploymentLogsHubMethods
{
    public const string Subscribe = nameof(Subscribe);
}

public interface IApplicationDeploymentLogsClient
{
    Task OnDeploymentLogs(DeploymentLiveLogSnapshot snapshot);
}

public sealed record DeploymentLiveLogLine(DateTimeOffset Timestamp, string Message, DeploymentStage? Stage = null);

/// <summary>A bounded tail; Version orders overlapping subscription replies and live deliveries.</summary>
public sealed record DeploymentLiveLogSnapshot(Guid OperationId, long Version,
    IReadOnlyList<DeploymentLiveLogLine> Lines, bool Truncated);
