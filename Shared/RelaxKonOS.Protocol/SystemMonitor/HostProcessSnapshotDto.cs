using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>Raw Helper measurements; CPU differences are calculated by the long-lived Server.</summary>
public sealed record HostProcessSnapshotDto(
    [property: JsonPropertyName("items")] IReadOnlyList<HostProcessSampleDto> Items,
    [property: JsonPropertyName("sampledAt")] DateTimeOffset SampledAt);

public sealed record HostProcessSampleDto(
    [property: JsonPropertyName("process")] ProcessInfoDto Process,
    [property: JsonPropertyName("cpuTicks")] long? CpuTicks);
