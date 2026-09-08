using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer.Models;

public sealed record ExplorerOperationFailure(string Path, string Message);
public sealed record ExplorerBatchResult(
    int Total,
    IReadOnlyList<FileSystemEntryDto> Completed,
    IReadOnlyList<ExplorerOperationFailure> Failures,
    int NotStarted);
