using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Server.Domain;

/// <summary>Persisted desired state for one schema-approved configuration value.</summary>
public sealed class RegistryEntry
{
    public Guid UserId { get; set; }
    public RegistryScope Scope { get; set; }
    public Guid ScopeId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RegistryValueType ValueType { get; set; }
    public string ValueJson { get; set; } = "null";
    public long Revision { get; set; } = 1;
    public RegistryEntryState State { get; set; } = RegistryEntryState.Synced;
    public DateTimeOffset DesiredUpdatedAt { get; set; }
    public string DesiredUpdatedBy { get; set; } = string.Empty;
    public long? AppliedRevision { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
}
