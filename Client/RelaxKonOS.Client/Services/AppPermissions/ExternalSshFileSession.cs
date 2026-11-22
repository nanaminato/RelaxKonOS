using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ExternalSshFileSession(
    ISshFileSession session, ISshFileConnection connection, CancellationToken lifetime) : ISshFileSession
{
    public bool IsConnected => !lifetime.IsCancellationRequested && ReferenceEquals(session.Connection, connection);
    public ISshFileConnection? Connection => IsConnected ? _connectionReference : null;

    private readonly ConnectionReference _connectionReference = new();
    private sealed class ConnectionReference : ISshFileConnection;

    private ISshFileConnection RequireConnection(ISshFileConnection requested)
    {
        lifetime.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_connectionReference, requested) || !IsConnected)
            throw new InvalidOperationException("SSH session ended.");
        return connection;
    }

    public Task<(string Path, SshDirectoryItem[] Items)> ReadDirectoryAsync(
        ISshFileConnection endpoint, string path, bool resolvePath = false) =>
        session.ReadDirectoryAsync(RequireConnection(endpoint), path, resolvePath);

    public Task<bool> CreateDirectoryAsync(ISshFileConnection endpoint, string path) =>
        session.CreateDirectoryAsync(RequireConnection(endpoint), path);

    public Task<bool> RenamePathAsync(ISshFileConnection endpoint, string path, string target) =>
        session.RenamePathAsync(RequireConnection(endpoint), path, target);

    public Task DeletePathsAsync(ISshFileConnection endpoint, IReadOnlyList<(string Path, bool Directory)> paths) =>
        session.DeletePathsAsync(RequireConnection(endpoint), paths);

    public Task<bool> UploadFileAsync(ISshFileConnection endpoint, Stream input, string target, Action<ulong> progress) =>
        session.UploadFileAsync(RequireConnection(endpoint), input, target, progress);

    public Task DownloadFileAsync(ISshFileConnection endpoint, string path, Stream output, Action<ulong> progress) =>
        session.DownloadFileAsync(RequireConnection(endpoint), path, output, progress);

    public Task<bool> UploadDirectoryAsync(ISshFileConnection endpoint, DirectoryInfo directory, string target,
        Action<string, long, long?> report, CancellationToken cancellationToken) =>
        session.UploadDirectoryAsync(RequireConnection(endpoint), directory, target, report, cancellationToken);

    public Task DownloadPathsAsync(ISshFileConnection endpoint, string directory,
        IReadOnlyList<(string Path, string Name, bool Directory)> paths, Action<string, long, long?> report,
        CancellationToken cancellationToken) =>
        session.DownloadPathsAsync(RequireConnection(endpoint), directory, paths, report, cancellationToken);

    public Task TransferPathsAsync(ISshFileConnection endpoint, string destination,
        IReadOnlyList<(string Path, string Name, bool Directory, bool Link)> paths, bool copy, Action<int, bool> moved) =>
        session.TransferPathsAsync(RequireConnection(endpoint), destination, paths, copy, moved);
}
