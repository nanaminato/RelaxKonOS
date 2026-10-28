using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>
/// One official server module as the running process sees it, plus the two reasons a disable request
/// would be refused. This is a read-only report: modules are switched only by configuration and a
/// restart, and no field here changes runtime state.
///
/// <para>
/// <see cref="OrphanedWork"/> lists durable operations that only this module can finish or reconcile;
/// while it is non-empty, starting with the module disabled stops on purpose. <see cref="RequiredBy"/>
/// lists enabled modules that declare a hard dependency on this one, because disabling it would fail
/// the next startup instead. Both lists let a client explain the consequence before the operator
/// edits configuration and restarts, which is where the failure would otherwise surface.
/// </para>
/// </summary>
public sealed record ServerModuleDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool Enabled,
    /// <summary>Capabilities this module provides while enabled; a subset of the host's own list.</summary>
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("requires")] IReadOnlyList<string> Requires,
    [property: JsonPropertyName("requiredBy")] IReadOnlyList<string> RequiredBy,
    [property: JsonPropertyName("orphanedWork")] IReadOnlyList<string> OrphanedWork);
