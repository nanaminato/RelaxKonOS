namespace RelaxKonOS.AppSDK;

/// <summary>Read-only operational alert summary and first page for the connected server.</summary>
public interface IServerAlerts
{
    Task<ServerAlertsResult> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed record ServerAlertsResult(AppCapabilityResult Status, ServerAlertsSnapshot? Snapshot = null);

public sealed record ServerAlertsSnapshot(int OpenCount, int AcknowledgedCount, int CriticalCount,
    IReadOnlyList<ServerAlert> Alerts);

public sealed record ServerAlert(string Severity, string Type, string ProblemCode, string Status, int OccurrenceCount);
