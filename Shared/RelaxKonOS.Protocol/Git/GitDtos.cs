using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Git;

/// <summary>Registered repository metadata + summary (list item).</summary>
public sealed record GitRepositoryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("currentBranch")] string? CurrentBranch = null,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch = null,
    [property: JsonPropertyName("headAhead")] int HeadAhead = 0,
    [property: JsonPropertyName("headBehind")] int HeadBehind = 0,
    [property: JsonPropertyName("hasUpstream")] bool HasUpstream = false,
    [property: JsonPropertyName("uncommittedCount")] int UncommittedCount = 0);

/// <summary>Repository detail with branch/upstream info.</summary>
public sealed record GitRepositoryDetailDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("currentBranch")] string CurrentBranch,
    [property: JsonPropertyName("upstreamBranch")] string? UpstreamBranch = null,
    [property: JsonPropertyName("remoteUrl")] string? RemoteUrl = null,
    [property: JsonPropertyName("isDetached")] bool IsDetached = false,
    [property: JsonPropertyName("isClean")] bool IsClean = true,
    [property: JsonPropertyName("aheadCount")] int AheadCount = 0,
    [property: JsonPropertyName("behindCount")] int BehindCount = 0);

/// <summary>Working tree status aggregate.</summary>
public sealed record GitStatusDto(
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("staged")] IReadOnlyList<GitFileChangeDto> Staged,
    [property: JsonPropertyName("unstaged")] IReadOnlyList<GitFileChangeDto> Unstaged,
    [property: JsonPropertyName("untracked")] IReadOnlyList<GitFileChangeDto> Untracked,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<GitFileChangeDto> Conflicts,
    [property: JsonPropertyName("upstream")] string? Upstream = null,
    [property: JsonPropertyName("ahead")] int Ahead = 0,
    [property: JsonPropertyName("behind")] int Behind = 0,
    [property: JsonPropertyName("isDetached")] bool IsDetached = false);

/// <summary>Single file change entry.</summary>
public sealed record GitFileChangeDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("oldPath")] string? OldPath = null,
    [property: JsonPropertyName("status")] string Status = "modified",
    [property: JsonPropertyName("staged")] bool Staged = false);

/// <summary>Branch item.</summary>
public sealed record GitBranchDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isRemote")] bool IsRemote = false,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent = false,
    [property: JsonPropertyName("isDefault")] bool IsDefault = false,
    [property: JsonPropertyName("tracking")] string? Tracking = null,
    [property: JsonPropertyName("ahead")] int Ahead = 0,
    [property: JsonPropertyName("behind")] int Behind = 0);

/// <summary>File-level comparison between a selected branch and the current working tree.</summary>
public sealed record GitBranchComparisonDto(
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("changedFiles")] IReadOnlyList<GitFileChangeDto> ChangedFiles);

/// <summary>Commit item (history list).</summary>
public sealed record GitCommitDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("shortSha")] string ShortSha,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("authorEmail")] string AuthorEmail,
    [property: JsonPropertyName("authorDate")] string AuthorDate,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("body")] string? Body = null);

/// <summary>Optional server-side constraints for the commit history.  Keeping these
/// on the log request avoids downloading an entire repository history just to filter
/// it in the desktop client.</summary>
public sealed record GitLogQuery(
    [property: JsonPropertyName("reference")] string? Reference = null,
    [property: JsonPropertyName("search")] string? Search = null,
    [property: JsonPropertyName("author")] string? Author = null,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("dateRange")] string? DateRange = null,
    [property: JsonPropertyName("caseSensitive")] bool CaseSensitive = false,
    [property: JsonPropertyName("useRegex")] bool UseRegex = false);

/// <summary>Single commit detail.</summary>
public sealed record GitCommitDetailDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("parents")] IReadOnlyList<string> Parents,
    [property: JsonPropertyName("changedFiles")] IReadOnlyList<GitFileChangeDto> ChangedFiles,
    [property: JsonPropertyName("body")] string? Body = null);

/// <summary>Single file diff text and stats.</summary>
public sealed record GitDiffDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("oldPath")] string? OldPath = null,
    [property: JsonPropertyName("patch")] string Patch = "",
    [property: JsonPropertyName("additions")] int Additions = 0,
    [property: JsonPropertyName("deletions")] int Deletions = 0,
    [property: JsonPropertyName("binary")] bool Binary = false,
    [property: JsonPropertyName("truncated")] bool Truncated = false);

/// <summary>Generic operation result for pull/push/merge/revert/checkout.</summary>
public sealed record GitOperationResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("operation")] string Operation = "",
    [property: JsonPropertyName("conflicts")] IReadOnlyList<string>? Conflicts = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("requiresCredentials")] bool RequiresCredentials = false);

/// <summary>Conflict file item.</summary>
public sealed record GitConflictFileDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("baseVersion")] string? BaseVersion,
    [property: JsonPropertyName("oursVersion")] string? OursVersion,
    [property: JsonPropertyName("theirsVersion")] string? TheirsVersion,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("canEdit")] bool CanEdit);

public sealed record GitConflictStateDto(
    [property: JsonPropertyName("operation")] string? Operation,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

/// <summary>Single Git remote entry (origin/upstream/...).</summary>
public sealed record GitRemoteDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fetchUrl")] string FetchUrl,
    [property: JsonPropertyName("pushUrl")] string? PushUrl = null);

/// <summary>Probe result for an arbitrary path: is it a Git repo, current branch, configured remotes.</summary>
public sealed record GitRepositoryProbeDto(
    [property: JsonPropertyName("isRepository")] bool IsRepository,
    [property: JsonPropertyName("hasCommits")] bool HasCommits = false,
    [property: JsonPropertyName("currentBranch")] string? CurrentBranch = null,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch = null,
    [property: JsonPropertyName("remotes")] IReadOnlyList<GitRemoteDto>? Remotes = null);

/// <summary>Add or update a Git remote. <c>Name</c> is the remote name (origin/upstream/...);
/// <c>Url</c> is the fetch URL; <c>PushUrl</c> overrides the push URL if non-null.</summary>
public sealed record GitRemoteRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("pushUrl")] string? PushUrl = null);

// ── Request bodies ──

/// <summary>Register a new repository.</summary>
public sealed record GitRepositoryRegistration(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path);

/// <summary>Commit request.</summary>
public sealed record GitCommitRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
    [property: JsonPropertyName("amend")] bool Amend = false);

/// <summary>Create branch request.</summary>
public sealed record GitBranchCreateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("startPoint")] string? StartPoint = null,
    [property: JsonPropertyName("track")] bool Track = false,
    [property: JsonPropertyName("checkout")] bool Checkout = true,
    [property: JsonPropertyName("resetExisting")] bool ResetExisting = false);

/// <summary>Pull strategy request.</summary>
public sealed record GitPullRequest(
    [property: JsonPropertyName("strategy")] string Strategy = "merge",
    [property: JsonPropertyName("remote")] string? Remote = null,
    [property: JsonPropertyName("refspec")] string? Refspec = null,
    /// <summary>Optional local branch to update without checking it out. When supplied and it is not
    /// HEAD, the server fetches its upstream then advances it only when the update is fast-forward.</summary>
    [property: JsonPropertyName("branch")] string? Branch = null);

/// <summary>HTTPS credentials supplied only for the current Git operation. The server may retain them
/// in its protected per-user credential store when <see cref="GitPushRequest.SaveCredentials"/> is set.</summary>
public sealed record GitCredentialRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("saveCredentials")] bool SaveCredentials = true);

/// <summary>Push options. Credentials are used through a temporary askpass helper rather than command-line arguments.</summary>
public sealed record GitPushRequest(
    [property: JsonPropertyName("credentials")] GitCredentialRequest? Credentials = null,
    [property: JsonPropertyName("saveCredentials")] bool SaveCredentials = true,
    /// <summary>Local branch to push. Defaults to the checked-out branch.</summary>
    [property: JsonPropertyName("localBranch")] string? LocalBranch = null,
    /// <summary>Remote to push to. Defaults to the selected local branch's push remote/upstream remote.</summary>
    [property: JsonPropertyName("remote")] string? Remote = null,
    /// <summary>Destination branch on <see cref="Remote"/>. Defaults to the local branch's upstream name, then its own name.</summary>
    [property: JsonPropertyName("remoteBranch")] string? RemoteBranch = null);

/// <summary>Checkout request.</summary>
public sealed record GitCheckoutRequest(
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("createIfMissing")] bool CreateIfMissing = false);

/// <summary>Revert request.</summary>
public sealed record GitRevertRequest(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("noCommit")] bool NoCommit = false);

/// <summary>Resolve conflicts request.</summary>
public sealed record GitResolveRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("content")] string? Content = null);

public sealed record GitConflictOperationRequest(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("action")] string Action);

/// <summary>Move the current branch and index to a commit. Only <c>soft</c> and <c>mixed</c>
/// modes are exposed: this API deliberately never performs <c>git reset --hard</c>.</summary>
public sealed record GitResetRequest(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("mode")] string Mode = "mixed");

/// <summary>Restore tracked paths from a commit into the working tree. The default source is HEAD.</summary>
public sealed record GitRestoreRequest(
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
    [property: JsonPropertyName("source")] string? Source = null);

/// <summary>Paths to add to the Git index without creating a commit.</summary>
public sealed record GitStageRequest(
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

/// <summary>Paths whose index entries should be restored from HEAD while leaving their working-tree contents intact.</summary>
public sealed record GitUnstageRequest(
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

/// <summary>Merge a source branch into the current branch. <c>Strategy</c> values:
/// <c>merge</c> (default, respects repo config merge.ff), <c>no-ff</c> (always create merge commit),
/// <c>ff-only</c> (refuse to create merge commit), <c>squash</c> (squash changes into worktree, no commit).</summary>
public sealed record GitMergeRequest(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("strategy")] string Strategy = "merge",
    [property: JsonPropertyName("noCommit")] bool NoCommit = false,
    [property: JsonPropertyName("message")] string? Message = null);

/// <summary>Rename a branch. Equivalent to <c>git branch -m &lt;old&gt; &lt;newName&gt;</c>.
/// If renaming the current branch, the repository's current branch record is refreshed automatically.</summary>
public sealed record GitBranchRenameRequest(
    [property: JsonPropertyName("newName")] string NewName);

/// <summary>Set or unset the upstream tracking branch for a local branch.
/// Pass <c>Upstream = null</c> to remove the upstream (same as <c>git branch --unset-upstream &lt;name&gt;</c>).
/// Non-null <c>Upstream</c> accepts both <c>origin/foo</c> short form and <c>refs/remotes/origin/foo</c> full ref;
/// if <c>Remote</c> is provided a new upstream association will be built as <c>Remote/Branch</c> if that format is missing.</summary>
public sealed record GitBranchTrackingRequest(
    [property: JsonPropertyName("upstream")] string? Upstream = null,
    [property: JsonPropertyName("remote")] string? Remote = null,
    [property: JsonPropertyName("branch")] string? Branch = null);

// ── Git engine (host CLI) status & install ──

/// <summary>Host Git CLI availability probe result, analogous to <c>DockerStatusDto</c>.</summary>
public sealed record GitEngineStatusDto(
    [property: JsonPropertyName("isAvailable")] bool IsAvailable,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("executablePath")] string? ExecutablePath = null,
    [property: JsonPropertyName("canAutoInstall")] bool CanAutoInstall = false);

/// <summary>Auto-install response with operation status and incremental progress.</summary>
public sealed record GitEngineInstallResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");
