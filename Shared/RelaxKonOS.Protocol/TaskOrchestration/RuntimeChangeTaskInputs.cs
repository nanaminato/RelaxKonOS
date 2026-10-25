using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Protocol.TaskOrchestration;

/// <summary>Exact lifecycle selection; the Action ID determines the operation. No execution key is accepted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeTaskChangeInput(
    [property: JsonRequired, JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonRequired, JsonPropertyName("hostId")] string HostId,
    [property: JsonRequired, JsonPropertyName("expectedStateRevision")] long ExpectedStateRevision,
    [property: JsonRequired, JsonPropertyName("expectedCatalogRevision")] long ExpectedCatalogRevision,
    [property: JsonRequired, JsonPropertyName("expectedTrustRevision")] long ExpectedTrustRevision,
    [property: JsonRequired, JsonPropertyName("sourceId")] string? SourceId,
    [property: JsonRequired, JsonPropertyName("releaseId")] string? ReleaseId,
    [property: JsonRequired, JsonPropertyName("allowPrerelease")] bool AllowPrerelease,
    [property: JsonRequired, JsonPropertyName("keepData")] bool KeepData);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeTaskChangeImpact(bool MayRestartService, bool StopsService, bool RemovesProgram,
    bool PreservesBusinessData, bool DeletesBusinessData, bool CrossStepCompensationSupported);

/// <summary>Frozen domain semantics. A future dispatch binds a run/step key without resolving a new release.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeTaskFrozenChange(string RuntimeId, RuntimeChange Change, RuntimeTaskChangeInput Selection,
    RuntimeRelease? Release, string? PreviousReleaseId, string? CurrentVersion, string? ConfigurationIdentity, RuntimeTaskChangeImpact Impact,
    RuntimeDependencyReport? Dependencies);
