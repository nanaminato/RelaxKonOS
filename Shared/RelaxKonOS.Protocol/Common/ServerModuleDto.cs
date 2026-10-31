using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>Independent installation, desired configuration, effective process and residency facts.
/// Management changes the desired set; effective facts remain the startup snapshot until restart.</summary>
public sealed record ServerModuleDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("effectiveEnabled"), JsonRequired] bool EffectiveEnabled,
    /// <summary>Capabilities this module provides while enabled; a subset of the host's own list.</summary>
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("requires")] IReadOnlyList<string> Requires,
    [property: JsonPropertyName("requiredBy")] IReadOnlyList<string> RequiredBy,
    [property: JsonPropertyName("orphanedWork")] IReadOnlyList<string> OrphanedWork,
    /// <summary>
    /// Load state of an enabled module, or null for a disabled one. Reading this never loads a module.
    /// </summary>
    [property: JsonPropertyName("residency")] ServerModuleResidencyDto? Residency = null,
    [property: JsonRequired] bool DesiredEnabled = true,
    string Packaging = "bundled",
    string? InstalledVersion = null,
    string? DesiredVersion = null,
    string? EffectiveVersion = null,
    string? ArtifactDigest = null,
    string SourceId = "server-release",
    string TrustState = "host",
    string? ServiceId = null,
    string? ProcessGeneration = null,
    string? DesiredRevision = null,
    string? EffectiveRevision = null,
    bool RestartRequired = false,
    Guid? PendingOperationId = null,
    string RecoveryState = "none",
    DateTimeOffset? ObservedAt = null,
    string ConfigurationOwner = "deployment",
    bool ConfigurationWritable = false,
    IReadOnlyList<ServerModuleActionAdmission>? AllowedActions = null,
    string? Name = null,
    string? Rid = null,
    string? HostApiRequirement = null,
    string ResourceReleaseLevel = "none",
    ServerModuleResidencyPolicyDto? DesiredResidencyPolicy = null,
    ServerModuleResidencyPolicyDto? EffectiveResidencyPolicy = null);
