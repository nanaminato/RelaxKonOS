using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments;

/// <summary>
/// Resource-key conventions for this app. Enum text lives under a stable per-family prefix, and a
/// server problem code is looked up by the code itself, so no translation table can drift from the
/// protocol's problem-code list.
/// </summary>
internal static class DeploymentText
{
    public const string Prefix = "application_deployments";
    public const string SourcePrefix = Prefix + ".source";
    public const string DesiredPrefix = Prefix + ".desired";
    public const string ActualPrefix = Prefix + ".actual";
    public const string WorkloadPrefix = Prefix + ".workload";
    public const string ReadinessPrefix = Prefix + ".readiness";
    public const string KindPrefix = Prefix + ".kind";
    public const string StatePrefix = Prefix + ".operation_state";
    public const string StagePrefix = Prefix + ".stage";

    /// <summary>An enum member key such as <c>application_deployments.stage.healthchecking</c>.</summary>
    public static string Enum(string prefix, Enum value) => $"{prefix}.{value.ToString().ToLowerInvariant()}";

    /// <summary>
    /// A problem code is its own resource key. When the server introduces a code this client has not
    /// translated yet, the operator still sees the code rather than a blank label.
    /// </summary>
    public static LocalizedStatus Problem(string? problemCode) =>
        string.IsNullOrWhiteSpace(problemCode) ? LocalizedStatus.Literal(string.Empty) : LocalizedStatus.Key(problemCode);
}
