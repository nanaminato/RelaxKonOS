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
    string? LastErrorMessage);

/// <summary>Read-model counts displayed by the built-in registry application.</summary>
public sealed record RegistrySummaryDto(int PendingSyncCount, int FailedCount, int RestartRequiredCount);

/// <summary>A direct child key returned for one opened registry key.</summary>
public sealed record RegistryKeyDto(RegistryScope Scope, string Path);

/// <summary>Creates an empty key. The server derives its concrete owner from the access token.</summary>
public sealed record CreateRegistryKeyRequest(RegistryScope Scope, string Path);

/// <summary>Stable REST routes for the server-owned registry control plane.</summary>
public static class RegistryApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
    public const string Entries = $"/{V1}/registry/entries";
    public const string Keys = $"/{V1}/registry/keys";
    public const string Summary = $"/{V1}/registry/summary";
}

/// <summary>Editable value payload. Scope ownership is always resolved from the caller token.</summary>
public sealed record PutRegistryEntryRequest(RegistryScope Scope, string Path, string Name, RegistryValueType ValueType, JsonElement Value, long? ExpectedRevision);
