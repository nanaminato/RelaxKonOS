using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Registry;

/// <summary>Owner boundary for a registry value. The server derives the concrete owner ID from the access token.</summary>
public enum RegistryScope { User, Workspace, Device }

/// <summary>Strong value type declared by the server-side registry schema.</summary>
public enum RegistryValueType { String, Boolean, Number, Json }

/// <summary>Projection lifecycle state of a desired registry value.</summary>
public enum RegistryEntryState { Synced, PendingSync, Applying, Failed, RestartRequired, Superseded }

/// <summary>How an applied value becomes visible to a running component.</summary>
public enum RegistryApplyMode { Immediate, RestartApplication, ReloadService, RestartServer }

public sealed record RegistryAddress(string ServerId, Guid OwnerUserId, RegistryScope Scope, Guid ScopeId, string KeyPath);
public sealed record RegistryContextDto(string ServerId, Guid CurrentUserId, string Username, Guid? WorkspaceId, bool CanDiscoverUsers);
public enum RegistryEditorKind { String, Boolean, Number, Integer, Enum, Color, StringList, Json }
public sealed record RegistryValueChoiceDto(string Label, JsonElement Value);
public sealed record RegistryValueSchemaDto(string ValueId, bool CanWrite, bool CanDelete, bool CanRename, JsonElement? DefaultValue)
{
    public RegistryEditorKind Editor { get; init; }
    public IReadOnlyList<RegistryValueChoiceDto> Choices { get; init; } = [];
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
}

/// <summary>A safe, schema-approved value visible to the current authenticated owner.</summary>
public sealed record RegistryEntryDto(
    RegistryScope Scope,
    string Path,
    string Name,
    RegistryValueType ValueType,
    JsonElement DesiredValue,
    long Revision,
    RegistryEntryState State,
    DateTimeOffset DesiredUpdatedAt,
    DateTimeOffset? AppliedAt,
    RegistryApplyMode ApplyMode,
    string? RestartTarget,
    string? LastErrorCode,
    string? LastErrorMessage)
{
    public RegistryAddress Address { get; init; } = null!;
    public string OwnerUsername { get; init; } = "";
    public RegistryValueSchemaDto Schema { get; init; } = null!;
}

/// <summary>Read-model counts displayed by the built-in registry application.</summary>
public sealed record RegistrySummaryDto(int PendingSyncCount, int FailedCount, int RestartRequiredCount);

/// <summary>A direct child key returned for one opened registry key.</summary>
public sealed record RegistryUserDto(Guid Id, string Username, bool IsCurrentUser, Guid? WorkspaceId);

public sealed record RegistryKeyDto(RegistryScope Scope, string Path)
{
    public RegistryAddress Address { get; init; } = null!;
    public bool CanCreateChild { get; init; }
    public bool CanDelete { get; init; }
}

/// <summary>Creates an empty key. The server derives its concrete owner from the access token.</summary>
public sealed record CreateRegistryKeyRequest(RegistryScope Scope, string Path);

/// <summary>Stable REST routes for the server-owned registry control plane.</summary>
public static class RegistryApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Context = $"/{V1}/registry/context";
    public const string Values = $"/{V1}/registry/users/{{userId:guid}}/values";
    public const string Keys = $"/{V1}/registry/users/{{userId:guid}}/keys";
    public const string Users = $"/{V1}/registry/users";
    public const string Summary = $"/{V1}/registry/users/{{userId:guid}}/summary";
    public static string ForUser(string route, Guid userId) => route.Replace("{userId:guid}", userId.ToString("D"));
}

/// <summary>Editable value payload. Scope ownership is always resolved from the caller token.</summary>
public sealed record PutRegistryEntryRequest(RegistryScope Scope, string Path, string Name, RegistryValueType ValueType, JsonElement Value, long? ExpectedRevision);
