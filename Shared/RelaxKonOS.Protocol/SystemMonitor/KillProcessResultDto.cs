using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>Terminates the confirmed PID/start-time instance. Host OS permissions remain authoritative.</summary>
public sealed record TerminateProcessRequest(
    [property: JsonPropertyName("expectedStartTime")] DateTimeOffset ExpectedStartTime);

/// <summary>Success requires observed exit. An unverified outcome must not be automatically replayed.</summary>
public sealed record KillProcessResultDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("requiresElevation")] bool RequiresElevation,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("error")] string? Error);
