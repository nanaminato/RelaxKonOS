using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// Canonical component ids. A component is one host-installed product the center manages;
/// several applications may depend on it, and one product may need different sources per platform.
/// </summary>
public static class ComponentIds
{
    public const string SingBox = "sing-box";
    public const string Mihomo = "mihomo";
    public const string Nginx = "nginx";
    public const string Squid = "squid";
    public const string Samba = "samba";
    public const string Docker = "docker";
    public const string Frpc = "frpc";
    public const string Frps = "frps";
    public const string Git = "git";
    public static bool Known(string? componentId) => componentId is SingBox or Mihomo or Nginx or Squid or Samba or Docker or Frpc or Frps or Git;
}

/// <summary>
/// How a host installs one component. It is not a preference: a target's platform decides which
/// modes exist at all (a Debian package on Linux and a managed archive on Windows are different
/// products with different service hosts).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComponentSourceMode>))]
public enum ComponentSourceMode { ManagedArchive, SystemPackage }

/// <summary>
/// A Debian package identity frozen from the host's own package manager. It names the exact bytes
/// that a fresh install would take right now — version, architecture, publishing repository, pool
/// location and digest — so a later transaction can confirm it is still installing that.
/// <paramref name="AlsoInstalls"/> is the rest of the transaction's own resolution (dependencies
/// that would be installed with it), so the plan can state its full effect instead of one name.
/// </summary>
public sealed record ComponentPackageArtifact(string FileName, string Uri, long Bytes, string Sha256);

public sealed record ComponentPackageTarget(
    string PackageName, string Version, string Architecture,
    string RepositoryId, string BaseUri, string Suite, string Area,
    string PoolUri, string Sha256, long Bytes, IReadOnlyList<ComponentPackageArtifact> AlsoInstalls);

/// <summary>
/// Exactly one member is populated, matching <paramref name="Mode"/>. A package freeze never
/// carries an archive release and vice versa; callers must not treat the other member as optional
/// detail.
/// </summary>
public sealed record ComponentPinnedArchive(string ReleaseId, string Version, string Rid, string SourceUri,
    string Sha256, string Format, long MaximumBytes, IReadOnlyList<string> AffectedComponents);

public sealed record ComponentFrozenTarget(ComponentSourceMode Mode, RuntimeRelease? Archive, ComponentPackageTarget? Package,
    ComponentPinnedArchive? Pin = null)
{
    public static ComponentFrozenTarget FromPin(ComponentPinnedArchive pin) => new(ComponentSourceMode.ManagedArchive, null, null, pin);
    public static ComponentFrozenTarget FromArchive(RuntimeRelease release) => new(ComponentSourceMode.ManagedArchive, release, null);
    public static ComponentFrozenTarget FromPackage(ComponentPackageTarget package) => new(ComponentSourceMode.SystemPackage, null, package);
}

/// <summary>
/// One component's normalized lifecycle state on this host. It exists so a planner or a caller can
/// talk about any component without knowing which product answered.
/// <paramref name="Revision"/> is the host's own change counter for a ledgered runtime. A package
/// has no ledger, so its revision is derived from the observed installed and candidate identities;
/// it therefore still changes when the host changes.
/// </summary>
public sealed record ComponentState(string ComponentId, string HostId, long Revision, RuntimeOwnership Ownership,
    ComponentSourceMode Mode, RuntimeTarget Target, string? ActiveVersion, bool RecoveryRequired);

/// <summary>
/// A component's declared modes plus its state, or why the state is not available. The modes are
/// reported even when nothing can be read, so a caller can tell "not on this platform" apart from
/// "this build has no adapter yet".
/// </summary>
public sealed record ComponentStateView(string ComponentId, ComponentState? State,
    IReadOnlyList<ComponentModeSupport> Modes, IReadOnlyList<string> Problems,
    IReadOnlyList<ComponentPinnedArchive>? PinnedArchives = null, long PinRevision = 0, Guid? CommittedOperationId = null, Guid? PendingOperationId = null);

/// <summary>
/// A request to freeze one change. Archive changes name a release and its catalog/trust revisions;
/// package changes name a version selector and have no catalog. Both name the state revision they
/// expect, so a concurrent change is a conflict rather than a silently different plan.
/// </summary>
public sealed record ComponentPlanRequest(string ComponentId, string HostId, RuntimeChange Change, long ExpectedRevision,
    ComponentSourceMode Mode, string? SourceId, long ExpectedSourceRevision, long ExpectedTrustRevision,
    string? ReleaseId, string? RequestedVersion, bool KeepData, string IdempotencyKey);

/// <summary>
/// A frozen proposal, not permission to execute. <paramref name="Target"/> is returned even when the
/// request is refused, so a refusal can name the exact candidate it would have installed.
/// <paramref name="Problems"/> are stable codes namespaced by <c>component.</c>.
/// </summary>
public sealed record ComponentPlanResult(ComponentPlanRequest? Request, ComponentState? State,
    ComponentFrozenTarget? Target, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool Allowed => Request is not null && Problems.Count == 0;
}

/// <summary>
/// What the host's package manager reports for one package right now. <paramref name="Candidate"/>
/// is what a fresh install would take; it is absent when the package has no installable candidate
/// (only an installed version, or nothing at all).
/// </summary>
public sealed record ComponentPackageResolution(string PackageName, string? InstalledVersion, ComponentPackageTarget? Candidate,
    bool Managed = false, bool RecoveryRequired = false, Guid? CommittedOperationId = null,
    Guid? PendingOperationId = null, ComponentRecoveryReceipt? RecoveryReceipt = null, string? BlockingProblem = null, ComponentPackageTarget? InstalledTarget = null, string? CandidateVersion = null, bool UpdateAvailable = false);

/// <summary>
/// Asks the Host to carry out exactly the change it just froze. <paramref name="Expected"/> is what
/// the caller was shown: a Host that now resolves something else must refuse instead of installing
/// it. <paramref name="Request"/> is re-planned on arrival, so the expectation can only narrow.
/// </summary>
public sealed record ComponentExecuteRequest(string ComponentId, ComponentPlanRequest Request,
    ComponentFrozenTarget? Expected);

[JsonConverter(typeof(JsonStringEnumConverter<ComponentExecutionOutcome>))]
public enum ComponentExecutionOutcome { Succeeded, Refused, Failed }

/// <summary>
/// <paramref name="Problems"/> names why nothing ran (Refused) or what broke (Failed);
/// <paramref name="Notes"/> carries facts the caller must know but that are not failures, such as
/// the absence of any rollback for a package transaction. <paramref name="Detail"/> is the domain's
/// own stable code when it has one, so an operator can tell "the configuration test failed" from
/// "the package manager refused" without reading host logs.
/// </summary>
public sealed record ComponentExecutionResult(string ComponentId, ComponentExecutionOutcome Outcome,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Notes, ComponentState? State, string? InstalledVersion,
    string? Detail = null)
{
    public bool Succeeded => Outcome == ComponentExecutionOutcome.Succeeded;
}

/// <summary>
/// Which modes this host can actually freeze for a component. <paramref name="Available"/> is false
/// when the product exists on that platform but this build has no adapter for it, and
/// <paramref name="ProblemCode"/> then names the reason instead of leaving a caller to guess.
/// </summary>
public sealed record ComponentModeSupport(ComponentSourceMode Mode, string Platform, bool Available, string? ProblemCode, string? PackageManager = null);

public static class ComponentRoutes
{
    public const string Root = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/components";
    public const string StatePattern = Root + "/{componentId}/state";
    public const string PlanPattern = Root + "/{componentId}/plan";
    public static string State(string componentId) => Root + "/" + componentId + "/state";
    public static string Plan(string componentId) => Root + "/" + componentId + "/plan";
}

public static class ComponentProblemCodes
{
    /// <summary>The component is not registered on this host at all.</summary>
    public const string UnknownComponent = "component.unknown";
    /// <summary>The component exists but not on this platform.</summary>
    public const string PlatformUnsupported = "component.platform_unsupported";
    /// <summary>The platform installs this component a different way than the request asked for.</summary>
    public const string SourceModeUnsupported = "component.source_mode_unsupported";
    /// <summary>The source mode is declared but this build has no lifecycle adapter for it yet.</summary>
    public const string ExecutionUnavailable = "component.execution_unavailable";
    /// <summary>Freezing worked but the host refuses the change for a domain reason.</summary>
    public const string PackageAlreadyInstalled = "component.package_already_installed";
    public const string PackageNoUpdate = "component.package_no_update";
    public const string PackageOwnershipRequired = "component.package_ownership_required";
    public const string PackageAbsent = "component.package_absent";
    public const string PackageUnresolved = "component.package_unresolved";
    public const string HostPolicyRequired = "component.host_policy_required";
    public const string PackageIndexUnavailable = "component.package_index_unavailable";
    public const string PackageVersionMismatch = "component.package_version_mismatch";
    public const string RequestInvalid = "component.request_invalid";
    public const string StateUnavailable = "component.state_unavailable";
    /// <summary>The host changed since the request was prepared; prepare the change again.</summary>
    public const string StateRevisionConflict = "component.state_revision_conflict";
    public const string RecoveryRequired = "component.recovery_required";
    /// <summary>The frozen identity is no longer what the host resolves; nothing was installed.</summary>
    public const string PackageExpectationChanged = "component.package_expectation_changed";
    /// <summary>The install ran and did not finish; the package manager owns whatever it left behind.</summary>
    public const string InstallFailed = "component.install_failed";
    /// <summary>The install reported success but the host measured a different version.</summary>
    public const string InstalledVersionMismatch = "component.installed_version_mismatch";
    /// <summary>A caller tried to execute a change this build only knows how to freeze.</summary>
    public const string ExecutionNotRequested = "component.execution_not_requested";
    /// <summary>Installation activates a service; the caller must have confirmed it.</summary>
    public const string ConfirmationRequired = "component.confirmation_required";

    /// <summary>Not a failure: a package transaction has no supported undo, and saying so is part of the result.</summary>
    public const string RollbackUnsupported = "component.rollback_unsupported";
}

public sealed record ComponentArchiveHostSnapshot(bool Installed, bool Managed, string? Version,
    IReadOnlyList<ComponentPinnedArchive> Releases, Guid? CommittedOperationId, bool RecoveryRequired);

[JsonConverter(typeof(JsonStringEnumConverter<ComponentRecoveryAction>))]
public enum ComponentRecoveryAction { Stop, Close, RefreshSources }
public sealed record ComponentRecoveryRequest(string ComponentId, Guid OperationId, ComponentRecoveryAction Action);
public sealed record ComponentRecoveryReceipt(Guid OperationId, Guid TargetOperationId, ComponentRecoveryAction Action);

/// <summary>Mutates an existing owned package at a measured prior commit.</summary>
public sealed record ComponentPackageChange(RuntimeChange Change, Guid PreviousOperationId, string ExpectedInstalledVersion);
