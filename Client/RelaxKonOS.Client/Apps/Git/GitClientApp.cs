using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Git.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Built-in client for managing Git repositories on the RelaxKonOS RelaxKonOS.Server.
/// 每个 Git Client 窗口都是一个独立项目实例（MultiWindow）——启动时显示项目选择器，
/// 用户从已注册项目打开，或新打开一个远程文件夹作为新项目；多个项目可同时在不同窗口运行。</summary>
public sealed class GitClientApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.git"), "Git Client", "0.2.0", "\U0001f33f",
        "Manage Git repositories on the RelaxKonOS Server",
        [AppPermissions.ServerGitRead, AppPermissions.ServerGitManage],
        InstancePolicy: ApplicationInstancePolicy.MultiWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteGitClient)) as IRemoteGitClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.git.display_name"),
                new GitLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        // 复用 Explorer 的 IExplorerClient，用于远程文件夹选择对话框（与 Code Editor 同一模式）
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var vm = new GitClientViewModel(client);
        ManagedWindow? window = null;

        // Wire dialog callbacks — the VM commands call these and handle the returned request objects.
        vm.ShowCommitDialogAsync = () => GitClientDialogs.ShowCommitDialogAsync(context, window!, vm);
        vm.ShowCreateBranchDialogAsync = source => GitClientDialogs.ShowCreateBranchDialogAsync(context, window!, vm, source);
        vm.ShowPullDialogAsync = () => GitClientDialogs.ShowPullDialogAsync(context, window!, vm);
        vm.ShowRegisterRepositoryDialogAsync = () => GitClientDialogs.ShowRegisterRepositoryDialogAsync(context, window!, vm);
        vm.ShowConfirmAsync = message => GitClientDialogs.ShowConfirmAsync(context, window!, message);
        vm.ShowMessageAsync = message => GitClientDialogs.ShowMessageAsync(context, window!, message);
        vm.ShowGitUnavailableAsync = async () =>
        {
            var shouldContinue = await GitClientDialogs.ShowGitUnavailableAsync(context, window!, vm);
            if (!shouldContinue)
            {
                // User cancelled / exited: close the window to leave the app.
                context.WindowManager.Close(window!);
            }
        };

        // 远程文件夹选择：直接复用内置 Explorer（ExplorerPickerMode.SelectFolder），
        // 与 Code Editor 的 RequestFolderAsync 完全一致；不再自实现一个简陋的目录浏览对话框。
        vm.ShowRemotePathPickerAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string?>(window!, LocalizedText.Get("git.dialog.select_folder.title"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.SelectFolder),
                    paths => dialog.Close(paths[0]))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, GetFolderPickerBounds(window!));
        vm.ShowInitConfirmAsync = path => GitClientDialogs.ShowInitConfirmAsync(context, window!, path);
        vm.ShowRemoteDialogAsync = existing => GitClientDialogs.ShowRemoteDialogAsync(context, window!, existing);
        vm.ShowRenameBranchDialogAsync = branch => GitClientDialogs.ShowRenameBranchDialogAsync(context, window!, branch);
        vm.ShowMergeDialogAsync = branch => GitClientDialogs.ShowMergeDialogAsync(context, window!, branch, vm);
        vm.ShowSetTrackingDialogAsync = branch => GitClientDialogs.ShowSetTrackingDialogAsync(context, window!, branch, vm);
        vm.ShowPushDialogAsync = () => GitClientDialogs.ShowPushDialogAsync(context, window!, vm);
        vm.ShowGitCredentialsDialogAsync = owner => GitClientDialogs.ShowGitCredentialsDialogAsync(context, owner ?? window!);
        vm.ShowRemoteBranchPickerDialogAsync = (owner, remote, branch) => GitClientDialogs.ShowRemoteBranchPickerDialogAsync(context, owner ?? window!, remote, branch, vm);

        var view = GitClientWorkspace.Create(vm);
        window = context.ShowWindow(LocalizedText.Get("application.remoteos.git.display_name"),
            view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        vm.ShowPrivilegedHelperUnavailableAsync = problemCode => PrivilegedHelperUnavailableDialog.ShowAsync(context, window, problemCode);
        _ = vm.StartAsync();
    }

    /// <summary>计算文件夹选择对话框的窗口边界（与 Code Editor 的 GetFilePickerBounds 一致）。</summary>
    private static Rect GetFolderPickerBounds(ManagedWindow owner)
    {
        var bounds = owner.Info.Bounds;
        const double width = 760;
        const double height = 520;
        var actualWidth = Math.Min(width, Math.Max(480, bounds.Width - 48));
        var actualHeight = Math.Min(height, Math.Max(320, bounds.Height - 56));
        return new Rect(
            bounds.X + Math.Max(24, (bounds.Width - actualWidth) / 2),
            bounds.Y + Math.Max(28, (bounds.Height - actualHeight) / 2),
            actualWidth,
            actualHeight);
    }
}
