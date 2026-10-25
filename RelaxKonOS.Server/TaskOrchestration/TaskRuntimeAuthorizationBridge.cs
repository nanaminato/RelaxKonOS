using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.TaskOrchestration;

public static class TaskRuntimeAuthorizationBridge
{
    public static async Task<SingBoxCurrentAuthorization> ReadAsync(TaskStepExecutionAuthorization authority,
        Func<CancellationToken, Task<SingBoxCurrentAuthorization>> published, CancellationToken ct)
    {
        try
        {
            var before = await authority.ValidateAsync(ct);
            var grant = await published(ct);
            var after = await authority.ValidateAsync(ct);
            var until = grant.ValidUntil < before ? grant.ValidUntil : before;
            if (after < until) until = after;
            if (until <= DateTimeOffset.UtcNow) throw new RuntimePolicyException("runtime.authorization_unavailable");
            return grant with { ValidUntil = until };
        }
        catch (Exception ex) when (ex is TaskApprovalException or TaskPlanStoreException)
        { throw new RuntimePolicyException("runtime.authorization_unavailable"); }
    }
}
