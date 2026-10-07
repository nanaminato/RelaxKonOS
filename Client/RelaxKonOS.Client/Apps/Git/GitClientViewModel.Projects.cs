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
            Log($"Probe 返回: IsRepository={probe.IsRepository} HasCommits={probe.HasCommits} Branch={probe.CurrentBranch} Remotes={probe.Remotes?.Count ?? 0}");
            if (!probe.IsRepository)
            {
                ProbeHint = LocalizedText.Get("git.vm.not_repo");
                var init = ShowInitConfirmAsync is not null && await ShowInitConfirmAsync(path);
                Log($"ShowInitConfirm 返回: {init}");
                if (!init)
                {
                    StatusText = LocalizedText.Ref("git.vm.init_cancelled");
                    return;
                }
                var initResult = await client.InitRepositoryAsync(path);
                Log($"git init 返回: Success={initResult.Success} Message={initResult.Message}");
                if (!initResult.Success)
                {
                    await NotifyAsync(LocalizedText.Format("git.vm.init_failed_format", initResult.Message));
                    return;
                }
                StatusText = LocalizedText.Ref("git.vm.initialized");
                probe = await client.ProbeRepositoryAsync(path);
                Log($"重探测后: IsRepository={probe.IsRepository} DefaultBranch={probe.DefaultBranch}");
            }

            // 已是 Git 仓库：检查是否已注册
            var existing = Repositories.FirstOrDefault(r =>
                string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Log($"路径已注册为「{existing.Name}」，直接打开");
                StatusText = LocalizedText.Ref("git.vm.project_exists_format", existing.Name);
                await OpenProjectCommand.ExecuteAsync(existing);
                return;
            }

            // 未注册：自动注册（名称取路径末段）
            var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = path;
            Log($"自动注册: name={name} path={path}");
            var dto = await client.RegisterRepositoryAsync(new GitRepositoryRegistration(name, path));
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

}
