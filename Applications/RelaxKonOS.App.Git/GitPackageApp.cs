using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.App.CodeEditor;
using RelaxKonOS.App.Git.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.App.Git;

public sealed class GitPackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.git"), "Project Editor", "0.3.0", "💻",
        "Edit project files and manage Git repositories on the RelaxKonOS Server",
        [AppPermissions.ServerGitRead, AppPermissions.ServerGitManage, AppPermissions.ServerFilesRead,
            AppPermissions.ServerFilesWrite, AppPermissions.ServerComponentsRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Git]),
        InstancePolicy: ApplicationInstancePolicy.MultiWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Label(string key) => GitLocalization.Resolve(language, key);
        if (!context.HasAuthenticatedServer || context.ServerFiles.IsSshSession)
        {
            context.Windows.ShowWindow(Label("git.title"),
                new TextBlock { Text = Label("git.login_required.message"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) },
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        if (context.Permissions.GetStatus(AppPermissions.ServerGitRead) == AppPermissionStatus.Undecided)
            await context.Permissions.RequestAsync(AppPermissions.ServerGitRead, cancellationToken);
        if (!context.Permissions.IsGranted(AppPermissions.ServerGitRead))
        {
            context.Windows.ShowWindow(Label("git.title"),
                new TextBlock { Text = Label("code_editor.permission_denied"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) },
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        if (context.Permissions.GetStatus(AppPermissions.ServerGitManage) == AppPermissionStatus.Undecided)
            await context.Permissions.RequestAsync(AppPermissions.ServerGitManage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var files = new SdkEditorWorkspaceFiles(context.ServerFiles, Label, context.Permissions);
        var vm = new GitClientViewModel(context.Git, Label);
        var project = new ProjectEditorViewModel(vm, context.Git, files, context.ServerFiles.IsPathCaseSensitive,
            context.TextEditorPreferences?.DefaultEncoding ?? "UTF-8", files.PathExistsAsync);
        var view = GitClientWorkspace.Create(vm, project);
        IExternalAppWindowHandle owner;
        try
        {
            owner = context.Windows.ShowWindow(Label("git.title"), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        }
        catch
        {
            project.Dispose();
            throw;
        }

        var closed = owner.Closed;
        var dialogs = new GitClientDialogs(new PackageDialogPresenter(owner), Label);
        view.BindWindow(owner.Window);
        dialogs.Configure(owner.Window, vm);
        CodeEditorPackageApp.ConfigureEditorDialogs(context, owner, project.Editor, files, Label);
        vm.RequestOpenHostingUriAsync = uri =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        };
        vm.ShowRemotePathPickerAsync = async () =>
        {
            if (context.Permissions.GetStatus(AppPermissions.ServerFilesRead) == AppPermissionStatus.Undecided)
                await context.Permissions.RequestAsync(AppPermissions.ServerFilesRead, closed);
            var result = await context.ServerFiles.PickAsync(owner,
                new(Label("git.dialog.select_folder.title"), ServerFilePickerMode.SelectFolder));
            return result.Status == AppCapabilityResult.Succeeded ? result.Path : null;
        };
        project.RequestDraftActionAsync = documents => owner.ShowDialogAsync<EditorDraftAction>(
            Label("git.editor.drafts_title"), dialog => new ProjectDraftDialog(
                documents.Select(item => item.Path ?? item.DisplayName).ToArray(), dialog.Close, Label), new Size(540, 360));
        project.Editor.RequestCloseDocumentAsync = async document =>
        {
            var action = await project.RequestDraftActionAsync([document]);
            return action == EditorDraftAction.Discard
                || action == EditorDraftAction.Save && await project.Editor.SaveDocumentAsync(document);
        };
        project.Editor.RequestDiscardChangesAsync = async document =>
            await project.RequestDraftActionAsync([document]) == EditorDraftAction.Discard;

        if (context.ComponentDependencies is { } dependencies)
            view.OpenSettings = () => _ = owner.ShowDialogAsync<bool>(Label("common.settings"),
                _ => dependencies.CreateSettingsView(owner) ?? new ContentControl(), new Size(620, 440));

        void RefreshLanguage(object? sender, SystemLanguageChangedEventArgs args)
        {
            void Apply()
            {
                if (closed.IsCancellationRequested) return;
                owner.Window.Title = Label("git.title");
                project.RefreshLocalization();
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        language.LanguageChanged += RefreshLanguage;
        closed.Register(() =>
        {
            language.LanguageChanged -= RefreshLanguage;
            view.DisposeGitDock();
            project.Dispose();
        });
        await vm.StartAsync();
    }

    private sealed class PackageDialogPresenter(IExternalAppWindowHandle window) : IGitDialogPresenter
    {
        public Task<TResult?> ShowDialogAsync<TResult>(ManagedWindow owner, string title,
            Func<ModalDialog<TResult>, Control> content, Size preferredSize) =>
            window.ShowOwnedDialogAsync(owner, title, content, preferredSize);
    }
}
