using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal static class WindowsFeatureInstallationState
{
    // MSFT_ServerManagerRequestState: InProgress=0, Completed=1, Failed=2.
    // null means that the caller must continue polling.
    internal static PrivilegedOperationResult? Evaluate(byte state, bool restartRequired) => state switch
    {
        0 => null,
        1 => restartRequired
            ? new(false, 1, Error: "Windows File Server role installation requires restart", ProblemCode: PrivilegedProblemCode.RestartRequired)
            : new(true),
        _ => new(false, 1, Error: $"Windows File Server role installation failed (RequestState={state})", ProblemCode: PrivilegedProblemCode.InternalError),
    };
}
