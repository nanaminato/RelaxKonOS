namespace RelaxKonOS.Protocol.TaskOrchestration;

public static class TaskPlanningRoutes
{
    public const string Preview = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/task-plans/preview";
    public const string Create = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/task-plans/plan";
    public const string PlanPattern = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/task-plans/{planId:guid}";
    public const string InvalidatePattern = PlanPattern + "/invalidate";
    public static string Plan(Guid id) => PlanPattern.Replace("{planId:guid}", id.ToString("D"), StringComparison.Ordinal);
    public static string Invalidate(Guid id) => Plan(id) + "/invalidate";
}
