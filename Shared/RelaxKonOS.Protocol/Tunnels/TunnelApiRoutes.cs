using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Tunnels;

/// <summary>Stable, host-side tunnel-management routes. Clients must use these constants.</summary>
public static class TunnelApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Tunnels = $"/{V1}/tunnels";
    public const string Profiles = $"{Tunnels}/profiles";
    public const string ProfilePattern = "/profiles/{profileId:guid}";
    public const string ProfilesPattern = "/profiles";
    public const string TunnelPattern = "/{tunnelId:guid}";
    public const string CollectionPattern = "";
    public const string ApplyProfile = $"{Profiles}/{{profileId}}/apply";
    public const string ApplyProfilePattern = "/profiles/{profileId:guid}/apply";
    public const string StopProfile = $"{Profiles}/{{profileId}}/stop";
    public const string StopProfilePattern = "/profiles/{profileId:guid}/stop";
    public const string ProfileSecret = $"{Profiles}/{{profileId}}/secret";
    public const string ProfileSecretPattern = "/profiles/{profileId:guid}/secret";
    public const string ProfileLogs = $"{Profiles}/{{profileId}}/logs";
    public const string ProfileLogsPattern = "/profiles/{profileId:guid}/logs";
    public const string Runtime = $"{Tunnels}/runtime";
    public const string RuntimePattern = "/runtime";
    public const string RuntimeDetectExternal = $"{Runtime}/external/detect";
    public const string RuntimeDetectExternalPattern = "/runtime/external/detect";
    public const string ManagedFrps = $"{Tunnels}/frps";
    public const string ManagedFrpsPattern = "/frps";
    public const string ManagedFrpsEditor = $"{ManagedFrps}/editor";
    public const string ManagedFrpsEditorPattern = "/frps/editor";
    public const string ManagedFrpsStart = $"{ManagedFrps}/start";
    public const string ManagedFrpsStartPattern = "/frps/start";
    public const string ManagedFrpsStop = $"{ManagedFrps}/stop";
    public const string ManagedFrpsStopPattern = "/frps/stop";
    public const string ManagedFrpsLogs = $"{ManagedFrps}/logs";
    public const string ManagedFrpsLogsPattern = "/frps/logs";
    public const string ManagedFrpsAudit = $"{ManagedFrps}/audit";
    public const string ManagedFrpsAuditPattern = "/frps/audit";
}
