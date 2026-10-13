using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Registry;

public sealed record ClientSynchronizationSettings(
    [property: JsonPropertyName("mode")] string Mode = "event",
    [property: JsonPropertyName("pollIntervalSeconds")] int PollIntervalSeconds = 300)
{
    public static ClientSynchronizationSettings Default { get; } = new();
    [JsonIgnore] public bool IsValid => Mode is "event" or "polling" or "manual" && PollIntervalSeconds is >= 60 and <= 3600;
}

public sealed record ServerRuntimeSettings(int RegistryFlushDelayMilliseconds = 5000,
    int NotificationCoalesceMilliseconds = 50, int RetryDelaySeconds = 5)
{
    public static ServerRuntimeSettings Default { get; } = new();
    [JsonIgnore] public bool IsValid => RegistryFlushDelayMilliseconds is >= 100 and <= 30000
        && NotificationCoalesceMilliseconds is >= 0 and <= 1000 && RetryDelaySeconds is >= 1 and <= 300;
}

public sealed record ServerRuntimeSnapshot(Guid InstanceId, long Revision, ServerRuntimeSettings Settings,
    Guid? UpdatedBy, DateTimeOffset UpdatedAt);
public sealed record PutServerRuntimeFieldRequest(Guid InstanceId, string Name, JsonElement Value, long ExpectedRevision);
public static class ServerRuntimeRoutes
{
    public const string Settings = "/api/v1.0/server/runtime-settings";
    public const string ElevationTarget = "server/runtime-settings";
}
