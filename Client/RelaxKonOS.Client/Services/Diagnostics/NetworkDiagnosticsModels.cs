namespace RelaxKonOS.Client.Services.Diagnostics;

/// <summary>State of the host-owned RelaxKonOS network recorder.</summary>
public sealed record NetworkDiagnosticsState(bool IsAvailable, bool IsRecording, string? UnavailableReason = null);

public enum NetworkDiagnosticsCommandResult { Succeeded, Unavailable }
public enum NetworkDiagnosticKind { Http, SignalR }
public enum NetworkDiagnosticOutcome { Succeeded, Failed, Cancelled, TransportError }

/// <summary>Payload captured for a RelaxKonOS network operation.</summary>
public sealed record NetworkDiagnosticPayload(string Content, string Format, long ByteLength);

/// <summary>One completed RelaxKonOS network operation captured by the developer inspector.</summary>
public sealed record NetworkDiagnosticEntry(
    long Id, DateTimeOffset StartedAt, TimeSpan Duration, NetworkDiagnosticKind Kind, string Source,
    string Name, string? Method, string PathAndQuery, NetworkDiagnosticOutcome Outcome, int? StatusCode,
    string? ContentType, long? DeclaredContentLength, bool IsMedia, string? ErrorKind,
    IReadOnlyDictionary<string, string>? RequestHeaders = null,
    IReadOnlyDictionary<string, string>? ResponseHeaders = null,
    NetworkDiagnosticPayload? RequestBody = null,
    NetworkDiagnosticPayload? ResponseBody = null,
    string? RequestUrl = null);

public sealed record NetworkDiagnosticsQuery(string? Text = null, NetworkDiagnosticKind? Kind = null,
    bool? IsMedia = null, bool? FailuresOnly = null);

public sealed record NetworkDiagnosticsSnapshot(NetworkDiagnosticsState State,
    IReadOnlyList<NetworkDiagnosticEntry> Entries, int DroppedEntryCount, int TotalEntryCount);
