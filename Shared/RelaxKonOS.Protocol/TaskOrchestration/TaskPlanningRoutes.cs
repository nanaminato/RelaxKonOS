namespace RelaxKonOS.Protocol.TaskOrchestration;

public static class TaskPlanningRoutes
{
    public const string Preview = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/task-plans/preview";
    public const string Create = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/task-plans/plan";
    public const string PlanPattern = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/task-plans/{planId:guid}";
    public const string InvalidatePattern = PlanPattern + "/invalidate";
    public const string ApprovePattern = PlanPattern + "/approve";
    public const string StartRunPattern = PlanPattern + "/run";
    public const string RunPattern = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/task-runs/{runId:guid}";
    public const string AdvancePattern = RunPattern + "/advance";
    public const string CancelRunPattern = RunPattern + "/cancel";
    public const string RecheckRecoveryPattern = RunPattern + "/recheck-recovery";
    public const string RecoveryPattern = RunPattern + "/recovery";
    public const string ApprovalPattern = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/task-approvals/{approvalId:guid}";
    public static string Plan(Guid id) => PlanPattern.Replace("{planId:guid}", id.ToString("D"), StringComparison.Ordinal);
    public static string Invalidate(Guid id) => Plan(id) + "/invalidate";
    public static string Approve(Guid id) => Plan(id) + "/approve";
    public static string StartRun(Guid id) => Plan(id) + "/run";
    public static string Run(Guid id) => RunPattern.Replace("{runId:guid}", id.ToString("D"), StringComparison.Ordinal);
    public static string Advance(Guid id) => Run(id) + "/advance";
    public static string CancelRun(Guid id) => Run(id) + "/cancel";
    public static string RecheckRecovery(Guid id) => Run(id) + "/recheck-recovery";
    public static string Recovery(Guid id) => Run(id) + "/recovery";
    public static string Approval(Guid id) => ApprovalPattern.Replace("{approvalId:guid}", id.ToString("D"), StringComparison.Ordinal);
}
