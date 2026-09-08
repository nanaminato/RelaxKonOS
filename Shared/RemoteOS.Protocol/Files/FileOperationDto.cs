namespace RemoteOS.Protocol.Files;

public enum FileOperationKind { Copy, Move, Delete }
public enum FileOperationState { Queued, Running, WaitingForDecision, Cancelling, Completed, CompletedWithIssues, Cancelled, Failed }
public enum FileOperationDecision { Retry, Skip, Replace, KeepBoth }
public sealed record FileOperationItem(string SourcePath, string? DestinationPath = null);
public sealed record StartFileOperationRequest(Guid RequestId, FileOperationKind Kind, IReadOnlyList<FileOperationItem> Items);
public sealed record FileOperationDecisionRequest(Guid IssueId, FileOperationDecision Action, bool ApplyToAll = false);
public sealed record FileOperationIssue(Guid Id, string Code, string SourcePath, string? DestinationPath,
    string Message, IReadOnlyList<FileOperationDecision> Choices);
public sealed record FileOperationDetail(string Path, string Outcome, string? Message = null);
public sealed record FileOperationDto(Guid Id, FileOperationKind Kind, FileOperationState State,
    IReadOnlyList<FileOperationItem> Items, string? CurrentPath, long CurrentBytes, long CurrentTotalBytes,
    int ProcessedItems, int SkippedItems, IReadOnlyList<string> CompletedSources,
    FileOperationIssue? Issue, IReadOnlyList<FileOperationDetail> Details, bool DetailsTruncated,
    DateTimeOffset CreatedAt, Guid RequestId = default, long Revision = 0)
{
    public bool IsTerminal => State is FileOperationState.Completed or FileOperationState.CompletedWithIssues
        or FileOperationState.Cancelled or FileOperationState.Failed;
}
