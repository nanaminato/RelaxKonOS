namespace RelaxKonOS.AppSDK;

/// <summary>An opaque reference to one SSH connection. Compare by reference.</summary>
public interface ISshFileConnection { }

public sealed record SshDirectoryItem(string Name, string Path, bool IsDirectory, bool IsLink, long Size, DateTime Modified);

public sealed class SshDirectoryAccessDeniedException(Exception cause) : IOException(cause.Message, cause);

/// <summary>File operations bound to an already verified SSH connection.</summary>
public interface ISshFileSession
{
    ISshFileConnection? Connection { get; }
    bool IsConnected { get; }

    Task<(string Path, SshDirectoryItem[] Items)> ReadDirectoryAsync(
        ISshFileConnection endpoint, string path, bool resolvePath = false);
    Task<bool> CreateDirectoryAsync(ISshFileConnection endpoint, string path);
    Task<bool> RenamePathAsync(ISshFileConnection endpoint, string path, string target);
    Task DeletePathsAsync(ISshFileConnection endpoint, IReadOnlyList<(string Path, bool Directory)> paths);

    Task<bool> UploadFileAsync(ISshFileConnection endpoint, Stream input, string target, Action<ulong> progress);
    Task DownloadFileAsync(ISshFileConnection endpoint, string path, Stream output, Action<ulong> progress);
    Task<bool> UploadDirectoryAsync(ISshFileConnection endpoint, DirectoryInfo directory, string target,
        Action<string, long, long?> report, CancellationToken cancellationToken);
    Task DownloadPathsAsync(ISshFileConnection endpoint, string directory,
        IReadOnlyList<(string Path, string Name, bool Directory)> paths, Action<string, long, long?> report,
        CancellationToken cancellationToken);
    Task TransferPathsAsync(ISshFileConnection endpoint, string destination,
        IReadOnlyList<(string Path, string Name, bool Directory, bool Link)> paths, bool copy, Action<int, bool> moved);
}
