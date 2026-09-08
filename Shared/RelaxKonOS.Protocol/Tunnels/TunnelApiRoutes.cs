using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Tunnels;

/// <summary>Stable, host-side tunnel-management routes. Clients must use these constants.</summary>
public static class TunnelApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
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
    public const string RuntimeInstallationStatus = $"{Runtime}/managed/install/status";
    public const string RuntimeInstallationStatusPattern = "/runtime/managed/install/status";
    public const string RuntimeDetectExternal = $"{Runtime}/external/detect";
    public const string RuntimeDetectExternalPattern = "/runtime/external/detect";
    public const string RuntimeInstall = $"{Runtime}/managed/install";
    public const string RuntimeInstallPattern = "/runtime/managed/install";
    public const string RuntimeDownload = $"{Runtime}/managed/download";
    public const string RuntimeDownloadPattern = "/runtime/managed/download";
    public const string RuntimeInstallFromFile = $"{Runtime}/managed/install/from-file";
    public const string RuntimeInstallFromFilePattern = "/runtime/managed/install/from-file";
    public const string RuntimeUninstall = $"{Runtime}/managed";
    public const string RuntimeUninstallPattern = "/runtime/managed";
    public const string RuntimeRollback = $"{Runtime}/managed/rollback";
    public const string RuntimeRollbackPattern = "/runtime/managed/rollback";
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
