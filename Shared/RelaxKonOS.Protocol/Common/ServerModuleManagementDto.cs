using System.Text.Json.Serialization;
using System.Text.Json;

namespace RelaxKonOS.Protocol.Common;

[JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleAction>))]
public enum ServerModuleAction { Enable, Disable, SetResidencyPolicy, Wake, TrySleep, Install, Update, Repair, Rollback, Remove }
[JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleOperationState>))]
public enum ServerModuleOperationState { Prepared, Applying, AwaitingRestart, Verifying, Succeeded, Failed, RecoveryRequired }

public sealed record ServerModuleBlocker(string ProblemCode, string? ModuleId = null);
public sealed record ServerModuleResidencyPolicyDto(string Strategy, int IdleGraceSeconds);
public sealed record ServerModuleActionAdmission([property: JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleAction>))] ServerModuleAction Action, bool Allowed, IReadOnlyList<ServerModuleBlocker> BlockedBy);
/// <summary>EnabledModuleIds is the complete exact desired set, never an incremental patch.</summary>
public sealed record ServerModulePlanRequest([property: JsonRequired] string ServiceId, [property: JsonRequired, JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleAction>))] ServerModuleAction Action,
    [property: JsonRequired] IReadOnlyList<string> ModuleIds, [property: JsonRequired] IReadOnlyList<string> EnabledModuleIds, [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey, string? ReleaseId = null, string? Strategy = null, int? IdleGraceSeconds = null);
public sealed record ServerModulePlanDto([property: JsonRequired] Guid PlanId, [property: JsonRequired] string ServiceId, [property: JsonRequired] ServerModulePlanRequest Target,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] DateTimeOffset ExpiresAt, [property: JsonRequired] bool RestartRequired,
    [property: JsonRequired] IReadOnlyList<ServerModuleBlocker> BlockedBy, [property: JsonRequired] IReadOnlyList<string> Added, [property: JsonRequired] IReadOnlyList<string> Removed);
public sealed record ServerModuleOperationDto([property: JsonRequired] Guid OperationId, [property: JsonRequired] Guid PlanId, [property: JsonRequired] string ServiceId,
    [property: JsonRequired, JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleOperationState>))] ServerModuleOperationState State, [property: JsonRequired] DateTimeOffset CreatedAt, [property: JsonRequired] DateTimeOffset UpdatedAt,
    [property: JsonRequired] string DesiredRevision, [property: JsonRequired] IReadOnlyList<string> EnabledModuleIds, string? ProblemCode = null,
    IReadOnlyList<ServerModuleActionResult>? ModuleResults = null);
public sealed record ServerModuleActionResult([property: JsonRequired] string ModuleId, [property: JsonRequired] bool Succeeded, string? ProblemCode = null);
public sealed record ServerModuleOperationPage([property: JsonRequired] IReadOnlyList<ServerModuleOperationDto> Items, [property: JsonRequired] int? NextOffset);
public sealed record ServerModulePlanStatusDto([property: JsonRequired] ServerModulePlanDto Plan, [property: JsonRequired] ServerModuleOperationDto? Operation);

/// <summary>Only the canonical current enum name is a wire value; numbers and aliases are refused.</summary>
public sealed class ServerModuleEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name
            && Enum.TryParse<T>(name, out var value) && Enum.IsDefined(value) && Enum.GetName(value) == name) return value;
        throw new JsonException("Invalid module enum value.");
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException("Invalid module enum value."));
}
public sealed record ServerModuleRecoveryReportDto(string ServiceId, string DesiredRevision,
    IReadOnlyList<string> DesiredEnabledModuleIds, IReadOnlyDictionary<string, ServerModuleResidencyPolicyDto> DesiredPolicies,
    IReadOnlyList<ServerModuleOperationDto> UnresolvedOperations, DateTimeOffset ObservedAt, string ObservationScope);

public static class ServerModuleManagementRoutes
{
    public const string Detail = ServerApiRoutes.Modules + "/{moduleId}";
    public const string Plans = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-plans";
    public const string Apply = Plans + "/{planId:guid}/apply";
    public const string Plan = Plans + "/{planId:guid}";
    public const string Operations = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-operations";
    public const string Operation = Operations + "/{operationId:guid}";
    public const string Releases = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-releases";
}
