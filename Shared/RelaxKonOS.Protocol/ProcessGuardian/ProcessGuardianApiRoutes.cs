using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ProcessGuardian;

/// <summary>Routes for the RelaxKonOS Guardian Agent facade.</summary>
public static class ProcessGuardianApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
    public const string Status = $"/{V1}/guardian/status";
    public const string Workloads = $"/{V1}/guardian/workloads";
    public const string Workload = $"/{V1}/guardian/workloads/{{id}}";
    public const string WorkloadAction = $"/{V1}/guardian/workloads/{{id}}/{{action}}";
    public const string WorkloadLogs = $"/{V1}/guardian/workloads/{{id}}/logs";
    public const string Audit = $"/{V1}/guardian/audit";
    public const string Services = $"/{V1}/guardian/services";
    public const string ServiceAction = $"/{V1}/guardian/services/{{id}}/{{action}}";
    public const string InstallationPlan = $"/{V1}/guardian/agent/installation/plan";
    public const string InstallationExecute = $"/{V1}/guardian/agent/installation/execute";
}
