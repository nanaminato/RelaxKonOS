using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Protocol.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<SettingsScope>))]
public enum SettingsScope { ClientDevice, Workspace, AppPrivate, HostUser, HostMachine }
[JsonConverter(typeof(JsonStringEnumConverter<SettingsCapabilityState>))]
public enum SettingsCapabilityState { Available, Unauthenticated, Offline, LoadingFailed, AccessDenied, ElevationRequired, HelperUnavailable, PlatformUnsupported, PolicyLocked, RecoveryUnavailable }
[JsonConverter(typeof(JsonStringEnumConverter<SettingsEffectiveState>))]
public enum SettingsEffectiveState { Immediate, NewProcess, NewLogin, ServiceRestart, HostRestart }
[JsonConverter(typeof(JsonStringEnumConverter<SettingsOperationState>))]
public enum SettingsOperationState { Prepared, Applying, Applied, Failed, PartiallyApplied, Unknown, AwaitingConfirmation, RolledBack, RecoveryRequired }

public sealed record SettingsTarget(string ResourceId, SettingsScope Scope, string? PlatformIdentity = null);
public sealed record SettingsCapability(SettingsCapabilityState State, string? ReasonCode = null);
public sealed record SettingDescriptor(string SettingId, string Category, string TitleKey, string DescriptionKey,
    string Route, SettingsScope Scope, string ValueType, SettingsCapability Capability, SettingsEffectiveState EffectiveState,
    IReadOnlyList<string> Keywords);
public sealed record SettingsCatalogSnapshot(IReadOnlyList<SettingDescriptor> Items, DateTimeOffset ObservedAt);
public sealed record HostTimeState(string TimeZoneId, IReadOnlyList<string> AvailableTimeZoneIds, string Revision,
    DateTimeOffset ObservedAt, string Provider);
public sealed record HostTimeSnapshot(HostTimeState Value, SettingsTarget Target, SettingsCapability Capability,
    SettingsEffectiveState EffectiveState = SettingsEffectiveState.Immediate);
public sealed record TimeZoneChange(string TimeZoneId);
public sealed record TimeZonePreviewRequest(string ExpectedRevision, string IdempotencyKey, TimeZoneChange Change);
public sealed record SettingsApplyRequest(Guid PlanId);
public sealed record SettingsRollbackRequest(string ExpectedRevision);
public sealed record SettingsDifference(string SettingId, string? Before, string? After);
public sealed record SettingsPlan(Guid PlanId, SettingsTarget Target, string ExpectedRevision, DateTimeOffset ExpiresAt,
    IReadOnlyList<SettingsDifference> Differences, HostElevationCapability RequiredCapability, string AuthorizationTarget,
    SettingsEffectiveState EffectiveState, string ImpactCode);
public sealed record SettingsOperation(Guid OperationId, string SettingId, SettingsTarget Target, SettingsOperationState State,
    DateTimeOffset UpdatedAt, string? ObservedRevision = null, string? ProblemCode = null,
    SettingsEffectiveState EffectiveState = SettingsEffectiveState.Immediate);

public static class SettingsApiRoutes
{
    private const string Root = "/" + RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Catalog = Root + "/settings/catalog";
    public const string Environment = Root + "/host-settings/environment";
    public const string EnvironmentPreview = Environment + "/preview";
    public const string EnvironmentApply = Environment + "/apply";
    public const string Time = Root + "/host-settings/time";
    public const string TimePreview = Time + "/preview";
    public const string TimeApply = Time + "/apply";
    public const string Operation = Root + "/settings/operations/{id}";
    public const string Rollback = Operation + "/rollback";
}

public static class SettingsRevisions
{
    /// <summary>Canonical resource content, not an in-memory increment; detects OS changes outside RelaxKonOS.</summary>
    public static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
