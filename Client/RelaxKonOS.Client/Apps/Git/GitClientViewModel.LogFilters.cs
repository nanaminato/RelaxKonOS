using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Services.Installation;
using System.Collections.ObjectModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git;

public sealed partial class GitClientViewModel
{
    /// <summary>Runs the history query currently shown in the toolbar.  This is also
    /// used after a branch/user/date selection so every visible result comes from
    /// Git, rather than an arbitrary client-side page of 100 commits.</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ApplyLogFiltersAsync()
    {
        if (SelectedRepository is null) return;
        _logFilterTimer?.Stop();
        var requestVersion = Interlocked.Increment(ref _logFilterRequestVersion);
        try
        {
            var commits = await client.GetLogAsync(SelectedRepository.Id, limit: 100, query: BuildLogQuery());
            if (requestVersion != Volatile.Read(ref _logFilterRequestVersion)) return;
            ReplaceCommits(commits);
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref _logFilterRequestVersion))
                StatusText = LocalizedText.Ref("git.vm.refresh_failed_format", ex.Message);
        }
    }

    private GitLogQuery BuildLogQuery() => new(
        Reference: SelectedLogBranch?.Value,
        Search: string.IsNullOrWhiteSpace(CommitSearchText) ? null : CommitSearchText.Trim(),
        Author: SelectedLogAuthor?.Value,
        Path: string.IsNullOrWhiteSpace(LogPathFilter) ? null : LogPathFilter.Trim(),
        DateRange: SelectedLogDate?.Value,
        CaseSensitive: IsLogCaseSensitive,
        UseRegex: IsLogRegex);

    private void ReplaceCommits(IReadOnlyList<GitCommitDto> commits)
    {
        var selectedSha = SelectedCommit?.Sha;
        Commits.Clear();
        foreach (var commit in commits) Commits.Add(commit);
        SelectedCommit = string.IsNullOrEmpty(selectedSha)
            ? null
            : Commits.FirstOrDefault(commit => string.Equals(commit.Sha, selectedSha, StringComparison.Ordinal));
        RebuildLogAuthorOptions();
    }

    private void RebuildLogBranchOptions()
    {
        var selectedValue = SelectedLogBranch?.Value;
        _updatingLogFilterOptions = true;
        try
        {
            LogBranchOptions.Clear();
            LogBranchOptions.Add(new GitLogFilterOption(string.Empty, "分支: HEAD"));
            foreach (var branch in Branches.OrderBy(branch => branch.IsRemote).ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase))
                LogBranchOptions.Add(new GitLogFilterOption(branch.Name, branch.Name));
            SelectedLogBranch = LogBranchOptions.FirstOrDefault(option => option.Value == selectedValue)
                ?? LogBranchOptions[0];
        }
        finally { _updatingLogFilterOptions = false; }
    }

    private void RebuildLogAuthorOptions()
    {
        var selectedValue = SelectedLogAuthor?.Value;
        var knownAuthors = LogAuthorOptions.Select(option => option.Value)
            .Concat(Commits.Select(commit => commit.Author))
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(author => author, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _updatingLogFilterOptions = true;
        try
        {
            LogAuthorOptions.Clear();
            LogAuthorOptions.Add(new GitLogFilterOption(string.Empty, "用户: 全部"));
            foreach (var author in knownAuthors)
                LogAuthorOptions.Add(new GitLogFilterOption(author, author));
            SelectedLogAuthor = LogAuthorOptions.FirstOrDefault(option => option.Value == selectedValue)
                ?? LogAuthorOptions[0];
        }
        finally { _updatingLogFilterOptions = false; }
    }

    partial void OnSelectedLogBranchChanged(GitLogFilterOption? value)
    {
        if (!_updatingLogFilterOptions && value is not null && SelectedRepository is not null)
            _ = ApplyLogFiltersAsync();
    }

    partial void OnSelectedLogAuthorChanged(GitLogFilterOption? value)
    {
        if (!_updatingLogFilterOptions && value is not null && SelectedRepository is not null)
            _ = ApplyLogFiltersAsync();
    }

    partial void OnSelectedLogDateChanged(GitLogFilterOption? value)
    {
        if (value is not null && SelectedRepository is not null)
            _ = ApplyLogFiltersAsync();
    }

    partial void OnCommitSearchTextChanged(string value) => QueueLogFilterRefresh();
    partial void OnLogPathFilterChanged(string value) => QueueLogFilterRefresh();
    partial void OnIsLogCaseSensitiveChanged(bool value) => QueueLogFilterRefresh();
    partial void OnIsLogRegexChanged(bool value) => QueueLogFilterRefresh();

    private void QueueLogFilterRefresh()
    {
        if (SelectedRepository is null) return;
        _logFilterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _logFilterTimer.Tick -= OnLogFilterTimerTick;
        _logFilterTimer.Tick += OnLogFilterTimerTick;
        _logFilterTimer.Stop();
        _logFilterTimer.Start();
    }

    private void OnLogFilterTimerTick(object? sender, EventArgs e)
    {
        _logFilterTimer?.Stop();
        _ = ApplyLogFiltersAsync();
    }

}
