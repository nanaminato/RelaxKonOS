using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
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
using RelaxKonOS.Core.Input;
using RelaxKonOS.Client.Apps.CodeEditor;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>Built-in client for managing Git repositories on the RelaxKonOS RelaxKonOS.Server.
/// 每个 Git Client 窗口都是一个独立项目实例（MultiWindow）——启动时显示项目选择器，
/// 用户从已注册项目打开，或新打开一个远程文件夹作为新项目；多个项目可同时在不同窗口运行。</summary>
public sealed class GitClientApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.git"), "Project Editor", "0.3.0", "\U0001f4bb",
        "Edit project files and manage Git repositories on the RelaxKonOS Server",
        [AppPermissions.ServerGitRead, AppPermissions.ServerGitManage, AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite],
        InstancePolicy: ApplicationInstancePolicy.MultiWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteGitClient)) as IRemoteGitClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.git.display_name"),
                new GitLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        // 复用 Explorer 的 IExplorerClient，用于远程文件夹选择对话框（与 Code Editor 同一模式）
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var vm = new GitClientViewModel(client);
        var manageComponent = RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterEntry.Navigate(context, "relaxkonos.git", "git");
        vm.OpenComponentCenter = manageComponent;
        var encodings = context.Services.GetService(typeof(TextEditorEncodingSettings)) as TextEditorEncodingSettings;
        var project = new ProjectEditorViewModel(vm, client, files,
            session.CurrentServer?.Platform != HostPlatformKind.Windows, encodings?.CodeEditorDefaultEncoding ?? "UTF-8");
        ManagedWindow? window = null;

        // Wire dialog callbacks — the VM commands call these and handle the returned request objects.
        vm.RequestOpenHostingUriAsync = uri =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        };
        vm.ShowWorkspaceInputAsync = (key, fields) => GitClientDialogs.ShowWorkspaceInputAsync(context, window!, key, fields);
        vm.ShowCommitDialogAsync = () => GitClientDialogs.ShowCommitDialogAsync(context, window!, vm);
        vm.ShowCreateBranchDialogAsync = source => GitClientDialogs.ShowCreateBranchDialogAsync(context, window!, vm, source);
        vm.ShowPullDialogAsync = () => GitClientDialogs.ShowPullDialogAsync(context, window!, vm);
        vm.ShowConflictResolutionDialogAsync = owner => GitClientDialogs.ShowConflictResolutionDialogAsync(context, owner ?? window!, vm);
        vm.ShowPushRejectedDialogAsync = owner => GitClientDialogs.ShowPushRejectedDialogAsync(context, owner ?? window!);
        vm.ShowRegisterRepositoryDialogAsync = () => GitClientDialogs.ShowRegisterRepositoryDialogAsync(context, window!, vm);
        vm.ShowConfirmAsync = message => GitClientDialogs.ShowConfirmAsync(context, window!, message);
        vm.ShowMessageAsync = message => GitClientDialogs.ShowMessageAsync(context, window!, message);
        vm.ShowGitUnavailableAsync = async () =>
        {
            await GitClientDialogs.ShowGitUnavailableAsync(context, window!, vm);
        };

        // 远程文件夹选择：直接复用内置 Explorer（ExplorerPickerMode.SelectFolder），
        // 与 Code Editor 的 RequestFolderAsync 完全一致；不再自实现一个简陋的目录浏览对话框。
        vm.ShowRemotePathPickerAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string?>(window!, LocalizedText.Get("git.dialog.select_folder.title"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.SelectFolder),
                    paths => dialog.Close(paths[0]), usageMemory: UsageMemoryStore.Capture(context), memoryPurpose: "GitClient.repository-folder")
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
        vm.ShowFileDiffAsync = (owner, diff) => GitClientDialogs.ShowFileDiffAsync(context, owner ?? window!, diff);
        vm.ShowCredentialAccountsAsync = () => GitClientDialogs.ShowCredentialAccountsAsync(context, window!, vm);
        vm.ShowGitCredentialsDialogAsync = (owner, host) => GitClientDialogs.ShowGitCredentialsDialogAsync(context, owner ?? window!, host);
        vm.ShowRemoteBranchPickerDialogAsync = (owner, remote, branch) => GitClientDialogs.ShowRemoteBranchPickerDialogAsync(context, owner ?? window!, remote, branch, vm);
        vm.ConfigureLogSession = log =>
        {
            log.ShowCredentialAccountsAsync = () => GitClientDialogs.ShowCredentialAccountsAsync(context, window!, log);
            log.ShowPushDialogAsync = () => GitClientDialogs.ShowPushDialogAsync(context, window!, log);
            log.ShowCreateBranchDialogAsync = branch => GitClientDialogs.ShowCreateBranchDialogAsync(context, window!, log, branch);
            log.ShowPullDialogAsync = () => GitClientDialogs.ShowPullDialogAsync(context, window!, log);
            log.ShowMergeDialogAsync = branch => GitClientDialogs.ShowMergeDialogAsync(context, window!, branch, log);
            log.ShowSetTrackingDialogAsync = branch => GitClientDialogs.ShowSetTrackingDialogAsync(context, window!, branch, log);
            log.ShowConflictResolutionDialogAsync = owner => GitClientDialogs.ShowConflictResolutionDialogAsync(context, owner ?? window!, log);
            log.ShowRemoteBranchPickerDialogAsync = (owner, remote, branch) => GitClientDialogs.ShowRemoteBranchPickerDialogAsync(context, owner ?? window!, remote, branch, log);
        };


        var view = GitClientWorkspace.Create(vm, project);
        window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.git.display_name"),
            RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterEntry.Wrap(view, manageComponent), new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        var localization = context.Services.GetService(typeof(LocalizationService)) as LocalizationService;
        EventHandler<SystemLanguageChangedEventArgs> languageChanged = (_, _) => project.RefreshLocalization();
        if (localization is not null) localization.LanguageChanged += languageChanged;
        CodeEditorSessionDialogs.Configure(context, window, project.Editor, files);
        project.RequestDraftActionAsync = async documents =>
        {
            return await context.ShowDialogAsync<EditorDraftAction>(window, LocalizedText.Get("git.editor.drafts_title"),
                dialog => new ProjectDraftDialog(documents.Select(item => item.Path ?? item.DisplayName).ToArray(), dialog.Close),
                new Size(540, 360));
        };
        project.Editor.RequestCloseDocumentAsync = async document =>
        {
            var action = await project.RequestDraftActionAsync([document]);
            return action == EditorDraftAction.Discard || action == EditorDraftAction.Save && await project.Editor.SaveDocumentAsync(document);
        };
        project.Editor.RequestDiscardChangesAsync = async document =>
            await project.RequestDraftActionAsync([document]) == EditorDraftAction.Discard;
        window.CanCloseAsync = async () => project.CanClose && await project.PrepareOperationAsync() && project.CanClose;
        window.KeyDown += (_, e) =>
        {
            if (!e.Handled && !e.IsRepeat && e.Modifiers == RemoteKeyModifiers.Alt)
            {
                if (e.Key == RemoteKey.Left) { view.NavigateHistory(-1); e.Handled = true; return; }
                if (e.Key == RemoteKey.Right) { view.NavigateHistory(1); e.Handled = true; return; }
                if (e.Key == RemoteKey.Digit(1)) { view.ToggleProjectTool(); e.Handled = true; return; }
                if (e.Key == RemoteKey.Digit(0)) { view.ToggleCommitTool(); e.Handled = true; return; }
                if (e.Key == RemoteKey.Digit(9)) { view.ToggleGitTool(); e.Handled = true; return; }
            }
            if (!e.Handled && !e.IsRepeat && e.Modifiers == RemoteKeyModifiers.Control && e.Key == RemoteKey.Letter('K'))
            { view.ShowCommitTool(); e.Handled = true; return; }
            if (!e.Handled && !e.IsRepeat && e.Key == RemoteKey.Letter('F') && e.Modifiers == RemoteKeyModifiers.Control)
            { view.EditorView.FocusFind(); e.Handled = true; return; }
            if (!e.Handled && !e.IsRepeat && e.Key == new RemoteKey("Tab")
                && (e.Modifiers == RemoteKeyModifiers.Control || e.Modifiers == (RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift)))
            { view.EditorView.CycleDocument(e.Modifiers.HasFlag(RemoteKeyModifiers.Shift) ? -1 : 1); e.Handled = true; return; }
            _ = WindowShortcut.TryExecute(e, RemoteKey.Letter('N'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Alt, vm.CreateBranchCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control, project.Editor.SaveCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('O'), RemoteKeyModifiers.Control, project.Editor.OpenDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('T'), RemoteKeyModifiers.Control, vm.PullCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('K'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift, vm.PushCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift, project.Editor.SaveAllCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('N'), RemoteKeyModifiers.Control, project.Editor.NewDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('W'), RemoteKeyModifiers.Control, project.Editor.CloseDocumentCommand);
        };
        EventHandler<ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            if (localization is not null) localization.LanguageChanged -= languageChanged;
            view.DisposeGitDock();
            project.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
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
