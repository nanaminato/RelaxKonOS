using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>
/// Residency of an enabled module's object graph: whether it is loaded, why it is not, when it last
/// served a request and how long the last wake took. This is a separate fact from installation and
/// enablement — a <c>dormant</c> module is still installed and enabled and wakes on the next business
/// request.
///
/// <para>
/// <see cref="State"/> is one of <c>dormant</c>, <c>loading</c>, <c>active</c>, <c>draining</c>,
/// <c>unloading</c>, <c>faulted</c> or <c>resident</c> (the module declares no release boundary).
/// <see cref="BlockedBy"/> explains why an idle module could not be released yet, for example because a
/// background build is still running; <see cref="Failure"/> keeps the last load failure visible so the
/// client can show a reason instead of a missing feature.
/// </para>
/// </summary>
public sealed record ServerModuleResidencyDto(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("releasable")] bool Releasable,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("idleGraceSeconds")] int IdleGraceSeconds,
    [property: JsonPropertyName("activeLeases")] int ActiveLeases,
    [property: JsonPropertyName("lastUsedAt")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("lastWakeMilliseconds")] double? LastWakeMilliseconds,
    [property: JsonPropertyName("blockedBy")] string? BlockedBy,
    [property: JsonPropertyName("failure")] string? Failure,
    [property: JsonPropertyName("lastReleasedAt")] DateTimeOffset? LastReleasedAt);
