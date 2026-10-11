using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Client.Apps.TaskManager;

/// <summary>Only an explicit permission failure may escalate a termination request.</summary>
internal static class ProcessTerminationFlow
{
    public static async Task<KillProcessResultDto> ExecuteAsync(bool administratorMode,
        Func<bool, Task<KillProcessResultDto>> terminate,
        Func<Func<Task<KillProcessResultDto>>, Task<KillProcessResultDto>> withElevation,
        Func<bool> isCurrent)
    {
        Task<KillProcessResultDto> Attempt(bool elevated)
        {
            if (!isCurrent()) throw new OperationCanceledException();
            return terminate(elevated);
        }

        if (administratorMode) return await withElevation(() => Attempt(true));
        var result = await Attempt(false);
        if (result.Success || !result.RequiresElevation) return result;
        return await withElevation(() => Attempt(true));
    }
}
