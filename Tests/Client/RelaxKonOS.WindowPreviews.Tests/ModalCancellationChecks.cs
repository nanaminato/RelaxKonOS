using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.App.Docker;
using RelaxKonOS.App.Docker.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using System.Reflection;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class ModalCancellationChecks
{
    public static void RunDocker(ShellSettings settings, ISystemLanguage language)
    {
        var manager = new WindowManagerService();
        manager.Attach(new Canvas { Width = 1100, Height = 800 });
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        using var http = new HttpClient();
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "docker-dialog-memory.json"));
        var factory = new ExternalAppContextFactory(null!, null!, language, settings, null!, auth,
            null!, manager, null!, new ExplorerClient(http, auth, memory), new SshDesktopSession(null!, settings),
            null!, null!, null!, null!, null!, memory, null!, null!, null!, null!);
        var application = new DockerPackageApp();
        var session = (EventAlertsAuthFixture)(object)auth;
        var originalLanguage = settings.Language;
        try
        {
            session.State = default;
            settings.Language = "en-US";
            application.ActivateAsync(factory.Create(application.Manifest)).GetAwaiter().GetResult();
            var signIn = manager.Windows.Single();
            Check(signIn.Title == "Docker Manager" && signIn.View.Content is DockerLoginRequiredView,
                "The package opens a localized sign-in window without server access.");
            settings.Language = "ja-JP";
            Dispatcher.UIThread.RunJobs();
            Check(signIn.Title == "Docker マネージャー", "The open sign-in window follows the workspace language.");
            manager.Close(signIn);
            session.State = AuthSessionState.Authenticated;
            application.ActivateAsync(factory.Create(application.Manifest)).GetAwaiter().GetResult();
            var workspace = manager.Windows.Single();
            Check(workspace.Title == "Docker マネージャー" && workspace.View.Content is DockerManagerWorkspace
                && workspace.View.HeaderCenter is TextBlock { Text: "Docker マネージャー" },
                "Authenticated package activation opens and localizes the Docker workspace.");
            manager.Close(workspace);
        }
        finally
        {
            settings.Language = originalLanguage;
        }
        var context = factory.Create(application.Manifest);
        var owner = context.Windows.ShowWindow("Docker", new TextBlock());
        Check(context.ComponentDependencies?.CreateSettingsView(owner) is ComponentDependencySettingsView
            { DataContext: ComponentDependencySettingsViewModel { HasComponents: true } },
            "Docker can display its host-provided component dependency settings.");
        var model = new DockerManagerViewModel(context.DockerResources, new DockerText(LocalizedText.Get));
        var result = DockerManagerDialogs.ShowDeployStackAsync(owner, model);
        var draft = manager.Windows.Last();
        model.ComposeYaml = "services: {}";
        model.IsLoading = true;
        manager.Close(draft);
        Check(manager.Windows.Count == 2 && !result.IsCompleted, "A busy Docker Compose draft ignores cancel.");
        model.IsLoading = false;

        manager.Close(draft);
        Check(manager.Windows.Count == 3 && !result.IsCompleted, "Cancel opens a nested confirmation for the Docker Compose draft.");
        var confirm = (ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!;
        confirm.NoCommand.Execute(null);
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 2; }, 2000);
        Check(!result.IsCompleted && model.ComposeYaml == "services: {}", "Keeping the Docker Compose draft retains its YAML.");

        manager.Close(draft);
        Check(manager.Windows.Count == 3, "The Docker Compose draft can request confirmation again.");
        confirm = (ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!;
        confirm.YesCommand.Execute(null);
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 1; }, 2000);
        Check(result.IsCompleted && manager.Windows.Count == 1, "Discarding the Docker Compose draft closes both modals.");
        manager.Close(owner.Window);
        Console.WriteLine("Docker Compose modal cancellation checks passed.");
    }

    public static void Run()
    {
        var manager = new WindowManagerService();
        manager.Attach(new Canvas { Width = 1000, Height = 700 });
        var owner = manager.Create(new WindowCreateOptions(
            new RelaxKonOS.Core.Applications.AppId("test.modal"), "Owner", new TextBlock(),
            new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 520)));
        ModalDialog<bool>? dialog = null;
        var result = manager.ShowDialogAsync<bool>(owner, "Draft", handle =>
        {
            dialog = handle;
            return new TextBox { Text = "unsaved" };
        });
        dialog!.CanCancelAsync = () => Task.FromResult(false);
        manager.Close(dialog.Window!);
        Check(!result.IsCompleted && manager.Windows.Count == 2, "Denied chrome close retains the draft window.");
        dialog.Cancel();
        Check(!result.IsCompleted, "Denied cancel button retains the draft.");

        var confirmation = new TaskCompletionSource<bool>();
        var calls = 0;
        dialog.CanCancelAsync = () => { calls++; return confirmation.Task; };
        var cancel = dialog.CancelAsync();
        dialog.Cancel();
        manager.Close(dialog.Window!);
        Check(calls == 1, "Repeated gestures share one pending cancellation check.");
        confirmation.SetResult(true);
        Dispatcher.UIThread.RunJobs();
        Check(cancel.IsCompletedSuccessfully && result.IsCompleted, "Confirmed cancellation completes the dialog.");
        // Modal cleanup is posted from a continuation on the thread pool.
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 1; }, 2000);
        Check(manager.Windows.Count == 1, "Confirmed cancellation removes the window and blocker.");

        var draft = manager.ShowDialogAsync<bool>(owner, "Dirty", handle =>
        {
            dialog = handle;
            RelaxKonOS.Client.Services.Dialogs.DraftDialogGuard.Attach(handle, () => false, () => true);
            return new TextBox { Text = "keep me" };
        });
        dialog!.Cancel();
        Check(manager.Windows.Count == 3 && !draft.IsCompleted, "Dirty cancellation opens an owned confirmation.");
        var confirmationVm = (RelaxKonOS.Client.Apps.Explorer.Dialogs.ConfirmDialogViewModel)
            ((Control)manager.Windows.Last().View.Content!).DataContext!;
        confirmationVm.NoCommand.Execute(null);
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 2; }, 2000);
        Check(!draft.IsCompleted && manager.Windows.Count == 2, "Keeping changes returns to the editor.");
        dialog.Cancel();
        confirmationVm = (RelaxKonOS.Client.Apps.Explorer.Dialogs.ConfirmDialogViewModel)
            ((Control)manager.Windows.Last().View.Content!).DataContext!;
        confirmationVm.YesCommand.Execute(null);
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 1; }, 2000);
        Check(draft.IsCompleted && manager.Windows.Count == 1, "Discarding changes closes the editor and both modal layers.");

        manager.ShowDialogAsync<bool>(owner, "Busy", handle =>
        {
            dialog = handle;
            handle.CanCancelAsync = () => Task.FromResult(false);
            return new TextBlock();
        });
        manager.Close(owner);
        Check(dialog!.Result.IsCompleted, "Owner teardown cancels even a busy dialog without prompting.");
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 0; }, 2000);
        Check(manager.Windows.Count == 0, "Owner teardown releases the modal chain.");

        // A handler can await network work between the click and the dialog request, so the
        // owner may already be gone. That must resolve to the default result, not throw:
        // the caller is an async-void click handler whose exception would end the process.
        var created = 0;
        var orphaned = manager.ShowDialogAsync<bool>(owner, "Orphan", _ =>
        {
            created++;
            return new TextBlock();
        });
        Check(orphaned.IsCompletedSuccessfully && orphaned.Result == false,
            "A dialog requested for a closed owner resolves to the default result.");
        Check(created == 0 && manager.Windows.Count == 0,
            "A dialog requested for a closed owner creates no window and no blocker.");
        Console.WriteLine("Modal cancellation checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
