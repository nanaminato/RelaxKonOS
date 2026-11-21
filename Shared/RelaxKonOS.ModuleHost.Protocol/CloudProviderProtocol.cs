namespace RelaxKonOS.ModuleHost.Protocol;

/// <summary>Private file operations between CloudStorage and the provider selected by the Host.</summary>
public static class CloudProviderProtocol
{
    public const string CallerModuleId = "cloud-storage";
    public const string WebDavModuleId = "cloud-webdav";
    public const string S3ModuleId = "cloud-s3";
    public const string BaiduModuleId = "cloud-baidu";
    public const string BaiduProtocol = "baidu-netdisk";
    public const string HostApiMinimum = "2.2.0";
    public const string BaiduHostApiMinimum = "2.3.0";
    public const string MediaType = "multipart/mixed";
    public const long MaximumUploadBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumMetadataBytes = 128 * 1024;
    public const long MaximumRequestBytes = MaximumUploadBytes + MaximumMetadataBytes + 8192;

    public static string ModuleId(CloudFileProvider provider) => provider switch
    {
        CloudFileProvider.WebDav => WebDavModuleId,
        CloudFileProvider.S3 => S3ModuleId,
        CloudFileProvider.Baidu => BaiduModuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static string RequestPath(CloudFileProvider provider) => "/internal/cloud-providers/" + ModuleId(provider);

    public static bool IsProviderModule(string moduleId) => moduleId is WebDavModuleId or S3ModuleId or BaiduModuleId;
}

public enum CloudFileProvider { WebDav, S3, Baidu }
public enum CloudProviderAction { List, Info, Download, CreateFolder, Move, Delete, Upload, StartAuthorization, PollAuthorization, Refresh, ResolveMutationPath }

public sealed record CloudProviderRequest(CloudProviderAction Action, string Endpoint, string UserName,
    string Secret, bool AllowPrivate, string Path, string? Cursor = null, string? Destination = null,
    bool Replace = false, long? UploadLength = null, string? Region = null, string? Bucket = null,
    string? Prefix = null, string? SessionToken = null, string? AccessToken = null)
{
    public override string ToString() => $"Cloud provider {Action}";
}
