namespace RelaxKonOS.Server.Proxy;

public interface IMihomoDirectPublicationExecutor
{
    Task<string?> PublishProfileAsync(DirectProfileExecution operation, DirectPublicationProblems problems);
    Task<string?> PublishSettingsAsync(DirectSettingsExecution operation, DirectPublicationProblems problems);
}

public sealed record DirectPublicationProblems(string RecoveryRequired, string ApplyFailed,
    string ControllerUnavailable, string PrivilegedOperationUnavailable, string SystemProxyConflict);

public sealed record DirectProfileExecution(
    Func<Task<string?>> ReloadAsync,
    Func<Task<bool>> IsHealthyAsync,
    Func<bool> HasPrevious,
    Action RestoreUnknown,
    Action RemoveCandidate,
    Action RestoreForCompensation,
    Func<Task<bool>> ConfirmAsync,
    Func<Task> CompleteAsync,
    Action<string> ReportCompensation);

public sealed record DirectSettingsExecution(
    bool TrackedHostPolicy,
    bool CanConfirmHostPolicy,
    bool ProxyDisabled,
    Func<Task<bool>> IsControllerAvailableAsync,
    Func<Task<string?>> ReloadAsync,
    Func<Task> RestoreAsync,
    Func<Task<bool>> ConfirmTrackedAsync,
    Func<Task<bool>> ConfirmAsync,
    Func<bool, Task<string?>> ApplyHostPolicyAsync,
    Func<Task<bool>> PersistAsync,
    Func<Task> CompleteAsync,
    Action<string> ReportHostPolicyFailure);
