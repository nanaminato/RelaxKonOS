using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.TaskOrchestration;

// A class intentionally has no generated record ToString containing administrator credentials.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ApproveTaskPlanRequest
{
    [JsonRequired] public string PlanDigest { get; init; } = "";
    [JsonRequired] public bool Confirmed { get; init; }
    [JsonRequired] public string? AdministratorUsername { get; init; }
    [JsonRequired] public string? Password { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StartTaskRunRequest([property: JsonRequired] Guid ApprovalId, [property: JsonRequired] string PlanDigest);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskRunRevisionRequest([property: JsonRequired] long ExpectedRevision);
