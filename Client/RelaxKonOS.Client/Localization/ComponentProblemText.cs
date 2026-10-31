using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Client.Localization;

/// <summary>
/// The one sentence per component problem code, shared by the component center's freeze panel and
/// the installation progress banner. A refused or failed component change is recorded in the durable
/// operation ledger as a <c>component.*</c> code, but the installation banner has no domain of its
/// own for those codes: without this table it can only fall back to "installation failed", which
/// hides the host's actual answer from the only screen that reports it.
/// </summary>
public static class ComponentProblemText
{
    /// <summary>True for the component lifecycle's own codes, never for installation or helper codes.</summary>
    public static bool IsComponentCode(string? code) =>
        code?.StartsWith("component.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Resolves any component problem code to a key that exists. An unrecognised code still gets a
    /// sentence: a failure nobody can name is not the same as no failure.
    /// </summary>
    public static string Key(string? code) => code switch
    {
        ComponentProblemCodes.UnknownComponent => "component_center.package.problem.unknown_component",
        ComponentProblemCodes.PlatformUnsupported => "component_center.package.problem.platform_unsupported",
        ComponentProblemCodes.SourceModeUnsupported => "component_center.package.problem.source_mode_unsupported",
        ComponentProblemCodes.ExecutionUnavailable => "component_center.package.problem.execution_unavailable",
        ComponentProblemCodes.ExecutionNotRequested => "component_center.package.problem.execution_not_requested",
        ComponentProblemCodes.ConfirmationRequired => "component_center.package.problem.confirmation_required",
        ComponentProblemCodes.PackageAlreadyInstalled => "component_center.package.problem.already_installed",
        ComponentProblemCodes.PackageNoUpdate => "component_center.native.no_update",
        ComponentProblemCodes.PackageIdentityUnverified => "component_center.package.identity_unverified",
        ComponentProblemCodes.PackageOwnershipRequired => "component_center.native.ownership_required",
        ComponentProblemCodes.PackageAbsent => "component_center.package.problem.absent",
        ComponentProblemCodes.PackageUnresolved => "component_center.package.problem.unresolved",
        ComponentProblemCodes.HostPolicyRequired => "component_center.package.problem.host_policy_required",
        ComponentProblemCodes.PackageIndexUnavailable => "component_center.package.problem.index_unavailable",
        ComponentProblemCodes.PackageVersionMismatch => "component_center.package.problem.version_mismatch",
        ComponentProblemCodes.PackageExpectationChanged => "component_center.package.problem.package_expectation_changed",
        ComponentProblemCodes.InstallFailed => "component_center.package.problem.install_failed",
        ComponentProblemCodes.InstalledVersionMismatch => "component_center.package.problem.installed_version_mismatch",
        ComponentProblemCodes.RollbackUnsupported => "component_center.package.problem.rollback_unsupported",
        ComponentProblemCodes.RequestInvalid => "component_center.package.problem.request_invalid",
        ComponentProblemCodes.StateUnavailable => "component_center.package.problem.state_unavailable",
        ComponentProblemCodes.StateRevisionConflict => "component_center.package.problem.revision_conflict",
        ComponentProblemCodes.RecoveryRequired => "component_center.package.problem.recovery_required",
        _ => "component_center.package.problem.unknown"
    };
}
