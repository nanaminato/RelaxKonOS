using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>One resource file the Host placed, named by its closed resource name rather than a path.</summary>
public sealed record RuntimeResourceFile(string Name, long Length, string Sha256);

/// <summary>
/// What the Host actually placed for the active program revision. It reports only the resource set the
/// Host owns; a user-supplied provider, certificate, or key file is never implied by this list.
/// </summary>
public sealed record RuntimeResourceReport(Guid RevisionId, IReadOnlyList<RuntimeResourceFile> Files);

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeResourceState>))]
public enum RuntimeResourceState { Missing, Verified, Invalid, Unknown }

/// <summary>Only verified copies carry content identities; observation failures never imply absence.</summary>
public sealed record RuntimeResourceObservation(string Name, RuntimeResourceState State,
    long? Length = null, string? Sha256 = null, string? ProblemCode = null);

public sealed record RuntimeResourceSnapshot(Guid? RevisionId, IReadOnlyList<RuntimeResourceObservation> Files,
    string? ProblemCode = null);
