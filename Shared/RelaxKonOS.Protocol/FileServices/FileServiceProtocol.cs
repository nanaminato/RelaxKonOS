using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.FileServices;

/// <summary>V1 deliberately exposes SMB only. Other file-transfer protocols have no contract yet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileServiceProtocol>))]
public enum FileServiceProtocol { Smb }

[JsonConverter(typeof(JsonStringEnumConverter<FileServiceRuntimeState>))]
public enum FileServiceRuntimeState { Unsupported, NotInstalled, Stopped, Running, Failed, Unavailable }

[JsonConverter(typeof(JsonStringEnumConverter<FileShareAccess>))]
public enum FileShareAccess { Read, ReadWrite }

/// <summary>Supported SMB lifecycle operations exposed by the control-plane contract.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SmbLifecycleAction>))]
public enum SmbLifecycleAction { Start, Stop, Restart }

public static class FileServiceProblemCodes
{
    public const string UnsupportedPlatform = "file-services.platform_unsupported";
    public const string NotInstalled = "file-services.smb.not_installed";
    public const string HelperUnavailable = "file-services.smb.helper_unavailable";
    public const string ConfigurationInvalid = "file-services.smb.configuration_invalid";
    public const string ConfigurationUnmanaged = "file-services.smb.configuration_unmanaged";
    public const string ShareConflict = "file-services.smb.share_conflict";
    public const string PortInUse = "file-services.smb.port_in_use";
    public const string PortUnavailable = "file-services.smb.port_unavailable";
    public const string ServiceStopped = "file-services.smb.service_stopped";
    public const string ServiceFailed = "file-services.smb.service_failed";
    public const string SystemAccountNotFound = "file-services.smb.system_account_not_found";
    public const string WindowsApiUnavailable = "file-services.smb.windows_api_unavailable";
    public const string WindowsServerRequired = "file-services.smb.windows_server_required";
    public const string RestartRequired = "file-services.smb.restart_required";
    public const string ReconciliationRequired = "file-services.smb.reconciliation_required";
    public const string ElevationRequired = "file-services.smb.elevation_required";
}
