namespace RelaxKonOS.ModuleHost.Protocol;

/// <summary>Private transport between the signed CloudStorage module and its WebDAV provider.</summary>
public static class CloudWebDavProtocol
{
    public const string CallerModuleId = "cloud-storage";
    public const string ProviderModuleId = "cloud-webdav";
    public const string RequestPath = "/internal/cloud-webdav";
    public const string MediaType = "multipart/mixed";
    public const long MaximumUploadBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumMetadataBytes = 128 * 1024;
    public const long MaximumRequestBytes = MaximumUploadBytes + MaximumMetadataBytes + 8192;
}

public enum CloudWebDavAction { List, Info, Download, CreateFolder, Move, Delete, Upload }

public sealed record CloudWebDavRequest(CloudWebDavAction Action, string Endpoint, string UserName,
    string Secret, bool AllowPrivate, string Path, string? Cursor = null, string? Destination = null,
    bool Replace = false, long? UploadLength = null)
{
    public override string ToString() => $"WebDAV {Action}";
}
