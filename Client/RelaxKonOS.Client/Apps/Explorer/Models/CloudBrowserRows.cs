using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>
/// The provider a connection speaks to. Every surface that names a cloud connection reads the
/// type through this one place, so the account rail, the overview card and the connection form
/// cannot disagree about what "baidu-netdisk" is called in the current language.
/// </summary>
public static class CloudProtocolText
{
    /// <summary>The protocol identifiers the server's cloud-storage module accepts.</summary>
    public const string WebDav = "webdav";
    public const string S3 = "s3";
    public const string BaiduNetdisk = "baidu-netdisk";

    public static string Name(string? protocol) => protocol?.Trim().ToLowerInvariant() switch
    {
        WebDav => LocalizedText.Get("explorer.cloud.protocol_webdav"),
        S3 => LocalizedText.Get("explorer.cloud.protocol_s3"),
        BaiduNetdisk => LocalizedText.Get("explorer.cloud.protocol_baidu"),
        { Length: > 0 } other => other,
        _ => LocalizedText.Get("explorer.cloud.protocol_unknown"),
    };

    /// <summary>
    /// Which form the connection editor shows. Unknown protocols fall back to the generic
    /// endpoint form instead of silently becoming a different provider.
    /// </summary>
    public static int FormIndex(string? protocol) => protocol?.Trim().ToLowerInvariant() switch
    {
        S3 => 1,
        BaiduNetdisk => 2,
        _ => 0,
    };

    /// <summary>OAuth providers authorize instead of accepting a stored password.</summary>
    public static bool UsesAuthorization(string? protocol) => FormIndex(protocol) >= 2;
}

/// <summary>A cloud connection as the account rail reads it: provider, name and write access.</summary>
public sealed class CloudAccountRow(CloudConnectionDto connection)
{
    public CloudConnectionDto Connection { get; } = connection;
    public Guid Id => Connection.Id;
    public string Name => Connection.Name;
    public string Protocol => Connection.Protocol;
    public string KindText => CloudProtocolText.Name(Connection.Protocol);
    public string AccessText => LocalizedText.Get(Connection.CanWrite
        ? "explorer.cloud.access_readwrite" : "explorer.cloud.access_readonly");
    public bool CanWrite => Connection.CanWrite;
    public override string ToString() => Name;
}

/// <summary>
/// One directory entry in a cloud listing. Sizes and timestamps are formatted here because the
/// providers report what they can: a folder has no size and an object store may omit the time.
/// </summary>
public sealed class CloudEntryRow(CloudEntryDto entry)
{
    public CloudEntryDto Entry { get; } = entry;
    public string Reference => Entry.Reference;
    public string Name => Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;
    public FileSystemEntryType EntryType =>
        Entry.IsDirectory ? FileSystemEntryType.Directory : FileSystemEntryType.File;
    public string SizeText => Entry.IsDirectory || Entry.Size is not > 0
        ? string.Empty : FileTransferMetrics.FormatBytes(Entry.Size.Value);
    public string ModifiedText => Entry.ModifiedAt is { } modified
        ? modified.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : string.Empty;
    public string KindText => LocalizedText.Get(Entry.IsDirectory
        ? "explorer.entry_type.directory" : "explorer.entry_type.file");
    public override string ToString() => Name;
}

/// <summary>
/// A crumb in the cloud path bar; the depth counts folders below the connection root, so the
/// last crumb is the folder currently open and is the one without a separator after it.
/// </summary>
public sealed record CloudBreadcrumb(string Label, int Depth, bool IsLast = false)
{
    public override string ToString() => Label;
}
