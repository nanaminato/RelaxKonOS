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
    /// <summary>在项目选择器中点击已注册的项目，进入工作区。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenProjectAsync(GitRepositoryDto repo)
    {
        Log($"OpenProjectAsync 开始: name={repo?.Name} id={repo?.Id}");
        if (repo is null) { Log("repo 为 null → 退出"); return; }
        if (OpenEditorProjectAsync is not null && !await OpenEditorProjectAsync(repo)) return;
        try
        {
            Log($"切换 IsPickerMode=false  SelectedRepository={repo.Name}");
            IsPickerMode = false;
            SelectedRepository = repo;
            ActivePage = GitClientPage.Overview;
            StatusText = LocalizedText.Ref("git.vm.loading_project_format", repo.Name);

            Log("调用 RefreshAllAsync（并行 status/branches/log）…");
            await RefreshAllAsync();
            await PresentConflictResolutionAsync();
            Log($"RefreshAllAsync 完成。Branches={Branches.Count} Commits={Commits.Count} " +
                $"Staged={StagedFiles.Count} Unstaged={UnstagedFiles.Count} Untracked={UntrackedFiles.Count}");

            Log("启动自动刷新（10s 轮询）…");
            StartStatusTimer();

            Log("调用 RefreshRemotesAsync…");
            await RefreshRemotesAsync();
            Log($"OpenProjectAsync 结束，Remotes={Remotes.Count}，Ready");
            StatusText = LocalizedText.Ref("git.vm.ready_project_branch_format", repo.Name, Status?.Branch ?? LocalizedText.Get("git.status.unknown_branch"));
        }
        catch (Exception ex)
        {
            await NotifyAsync(LocalizedText.Format("git.vm.open_project_failed_format", ex.Message));
            Log($"OpenProjectAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>打开远程文件夹选择器；选中后探测 Git 状态：是仓库则注册并打开；否则提示初始化。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenFolderAsync()
    {
        Log("OpenFolderAsync 开始");
        if (ShowRemotePathPickerAsync is null)
        {
            await NotifyAsync(LocalizedText.Get("git.vm.path_picker_unavailable"));
            Log("ShowRemotePathPickerAsync 委托未设置");
            return;
        }

        var path = await ShowRemotePathPickerAsync();
        Log($"路径选择返回: {(path is null ? "null" : $"\"{path}\"")}");
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!IsGitAvailable && OpenPlainFolderAsync is not null)
        {
            await OpenPlainFolderAsync(path);
            return;
        }

        await ProbeAndOpenAsync(path);
    }

    /// <summary>手动注册一个绝对路径作为 Git 项目（输入对话框形式）。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task RegisterRepositoryAsync()
    {
        Log("RegisterRepositoryAsync 开始");
        if (ShowRegisterRepositoryDialogAsync is null) { Log("ShowRegisterRepositoryDialogAsync=null"); return; }
        var registration = await ShowRegisterRepositoryDialogAsync();
        Log($"对话框返回: {(registration is null ? "null" : $"{registration.Name} @ {registration.Path}")}");
        if (registration is null) return;
        try
        {
            var dto = await client.RegisterRepositoryAsync(registration);
            if (!Repositories.Contains(dto))
                Repositories.Add(dto);
            StatusText = LocalizedText.Ref("git.vm.registered_format", dto.Name);
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.register_failed_format", ex.Message)); Log(ex.ToString()); }
    }

    private async Task ProbeAndOpenAsync(string path)
    {
        Log($"ProbeAndOpenAsync path={path}");
        IsProbing = true;
        IsBusy = true;
        ProbeHint = LocalizedText.Format("git.picker.probing_format", path);
        try
        {
            Log("调用 client.ProbeRepositoryAsync …");
            var probe = await client.ProbeRepositoryAsync(path);
            Log($"Probe 返回: IsRepository={probe.IsRepository} Problem={probe.Problem} HasCommits={probe.HasCommits} Branch={probe.CurrentBranch} Remotes={probe.Remotes?.Count ?? 0}");
            if (!probe.IsRepository)
            {
                ProbeHint = DescribeNotRepository(probe);
                // Git refused to open the path (owner mismatch, permission denied, boundary down). That
                // is not an empty directory: initialising would fail for the same reason and bury the
                // real cause, so the reason itself is the answer.
                if (!string.IsNullOrWhiteSpace(probe.Problem))
                {
                    await NotifyAsync(ProbeHint);
                    return;
                }
                if (OpenPlainFolderAsync is not null)
                {
                    await OpenPlainFolderAsync(path);
                    return;
                }

                if (!await ConfirmInitAsync(path)) return;

                if (!await TryInitializeAsync(path)) return;
                probe = await client.ProbeRepositoryAsync(path);
                Log($"重探测后: IsRepository={probe.IsRepository} DefaultBranch={probe.DefaultBranch} Problem={probe.Problem}");
                if (!probe.IsRepository)
                {
                    ProbeHint = DescribeNotRepository(probe);
                    await NotifyAsync(ProbeHint);
                    return;
                }
            }

            // 已是 Git 仓库：检查是否已注册
            var existing = Repositories.FirstOrDefault(r =>
                string.Equals(r.Path, path, RepositoryPathComparison));
            if (existing is not null)
            {
                Log($"路径已注册为「{existing.Name}」，直接打开");
                StatusText = LocalizedText.Ref("git.vm.project_exists_format", existing.Name);
                await OpenProjectCommand.ExecuteAsync(existing);
                return;
            }

            // 未注册：自动注册（名称取路径末段）
            var name = path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
            if (string.IsNullOrWhiteSpace(name)) name = path;
            Log($"自动注册: name={name} path={path}");
            GitRepositoryDto dto;
            try
            {
                dto = await client.RegisterRepositoryAsync(new GitRepositoryRegistration(name, path));
            }
            catch (Exception ex)
            {
                var message = LocalizedText.Format("git.vm.register_failed_format", ex.Message);
                ProbeHint = message;
                await NotifyAsync(message);
                Log($"注册异常：{ex.GetType().Name} {ex.Message}");
                return;
            }
            if (!Repositories.Contains(dto))
                Repositories.Add(dto);
            StatusText = LocalizedText.Ref("git.vm.project_registered_format", dto.Name);
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex)
        {
            await NotifyAsync(LocalizedText.Format("git.vm.probe_failed_format", ex.Message));
            ProbeHint = LocalizedText.Format("git.vm.probe_failed_format", ex.Message);
            Log($"ProbeAndOpenAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            IsProbing = false;
            IsBusy = false;
        }
    }

    public async Task InitializeEditorProjectAsync(string path)
    {
        if (!await PrepareEditorAsync() || !await ConfirmInitAsync(path)) return;
        if (await TryInitializeAsync(path)) await ProbeAndOpenAsync(path);
    }

    /// <summary>"Not a repository" and "git refused to open this repository" are different answers, and
    /// only the second one tells the user to fix permissions or ownership instead of initialising.</summary>
    private static string DescribeNotRepository(GitRepositoryProbeDto probe)
        => probe.Problem?.Contains("detected dubious ownership", StringComparison.OrdinalIgnoreCase) == true
            ? LocalizedText.Get("git.vm.ownership_mismatch")
            : string.IsNullOrWhiteSpace(probe.Problem)
            ? LocalizedText.Get("git.vm.not_repo")
            : LocalizedText.Format("git.vm.not_repo_reason_format", probe.Problem);

    private async Task<bool> ConfirmInitAsync(string path)
    {
        var init = ShowInitConfirmAsync is not null && await ShowInitConfirmAsync(path);
        Log($"ShowInitConfirm 返回: {init}");
        return init;
    }

    private async Task<bool> TryInitializeAsync(string path)
    {
        try
        {
            var initResult = await client.InitRepositoryAsync(path);
            Log($"git init 返回: Success={initResult.Success} Message={initResult.Message}");
            if (!initResult.Success)
            {
                var message = LocalizedText.Format("git.vm.init_failed_format", initResult.Message);
                ProbeHint = message;
                await NotifyAsync(message);
                return false;
            }
            StatusText = LocalizedText.Ref("git.vm.initialized");
            return true;
        }
        catch (Exception ex)
        {
            var message = LocalizedText.Format("git.vm.init_failed_format", ex.Message);
            ProbeHint = message;
            await NotifyAsync(message);
            Log($"init 异常：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

}
