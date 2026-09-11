using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Tunnels;

public enum TunnelProtocol { Tcp, Udp, Http, Https }
public enum TunnelAuthKind { None, Token }
public enum TunnelTlsMode { Default, Disable, Force }
public enum TunnelRuntimeMode { Managed, External }
public enum TunnelConnectionState { SavedNotApplied, Starting, Connected, Disconnected, RuntimeUnavailable, Unknown }
public enum TunnelRuntimeState { NotInstalled, Available, Running, Stopped, ExternalInvalid, Unknown }
public enum TunnelRuntimeInstallationState { Idle, Queued, Downloading, Copying, Verifying, Extracting, HealthChecking, Activating, Succeeded, Failed }

/// <summary>Profile projection. Token is populated only by the Controller-authorized profile editing endpoint.</summary>
public sealed record TunnelServerProfileDto(
    Guid Id, string Name, string Host, int Port, TunnelAuthKind AuthKind, bool TokenConfigured,
    TunnelTlsMode TlsMode, TunnelRuntimeMode RuntimeMode, string? ExternalExecutablePath,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Safe desired-state projection. It never contains generated TOML or credentials.</summary>
public sealed record TunnelDefinitionDto(
    Guid Id, Guid ServerProfileId, string Name, string ProviderId, TunnelProtocol Protocol,
    string LocalHost, int LocalPort, int? RemotePort, string? Domain, bool Enabled,
    bool Encryption, bool Compression, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    TunnelConnectionState State = TunnelConnectionState.SavedNotApplied, string ProblemCode = "");

public sealed record TunnelRuntimeDto(
    string RuntimeId, TunnelRuntimeMode Mode, TunnelRuntimeState State, string? Version,
    string? ExecutablePath, string ProblemCode = "", DateTimeOffset? StartedAt = null,
    string? PreviousVersion = null, bool IntegrityVerified = false);

/// <summary>Safe, host-wide progress projection for a managed FRP runtime installation.</summary>
public sealed record TunnelRuntimeInstallationDto(
    TunnelRuntimeInstallationState State, string? Version, int Progress,
    string ProblemCode = "", DateTimeOffset? UpdatedAt = null);

public sealed record TunnelOperationResultDto(bool Succeeded, TunnelConnectionState State, string ProblemCode = "");
public sealed record TunnelLogEntryDto(DateTimeOffset Timestamp, string Level, string Message);
public sealed record TunnelAuditEntryDto(DateTimeOffset Timestamp, string Action, string Result, string ProblemCode);

public enum ManagedFrpsState { NotConfigured, Stopped, Starting, Running, RuntimeUnavailable, Failed }
public sealed record TunnelPortRangeDto(int Start, int End);
/// <summary>Host-local frps projection. Token is populated only by Controller-authorized editing routes.</summary>
public sealed record ManagedFrpsConfigurationDto(
    string BindAddress, int BindPort, IReadOnlyList<TunnelPortRangeDto> AllowPorts,
    int? VhostHttpPort, int? VhostHttpsPort, bool ForceTls, bool TokenConfigured,
    bool DashboardEnabled, string DashboardAddress, int? DashboardPort, string? DashboardUser,
    bool DashboardPasswordConfigured, ManagedFrpsState State, string ProblemCode = "", DateTimeOffset? StartedAt = null,
    string? Token = null);
public sealed record UpdateManagedFrpsConfigurationRequest(
    bool Confirmed, string BindAddress, int BindPort, IReadOnlyList<TunnelPortRangeDto>? AllowPorts,
    int? VhostHttpPort, int? VhostHttpsPort, bool ForceTls, string? Token,
    bool DashboardEnabled, string DashboardAddress, int? DashboardPort, string? DashboardUser, string? DashboardPassword);

public sealed record UpsertTunnelServerProfileRequest(
    string Name, string Host, int Port, TunnelAuthKind AuthKind, TunnelTlsMode TlsMode,
    TunnelRuntimeMode RuntimeMode, string? ExternalExecutablePath, long? ExpectedRevision = null);

public sealed record SetTunnelProfileTokenRequest(string Token);

public sealed record UpsertTunnelDefinitionRequest(
    Guid ServerProfileId, string Name, TunnelProtocol Protocol, string LocalHost, int LocalPort,
    int? RemotePort, string? Domain, bool Enabled, bool Encryption, bool Compression,
    long? ExpectedRevision = null);

/// <summary>Explicit external-runtime detection request. It only inspects the specified absolute executable path.</summary>
public sealed record DetectExternalTunnelRuntimeRequest(string ExecutablePath);
