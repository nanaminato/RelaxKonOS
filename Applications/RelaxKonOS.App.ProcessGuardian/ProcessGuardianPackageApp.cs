using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.App.ProcessGuardian;

public sealed class ProcessGuardianPackageApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.processguardian.display_name";
    private const string HelperKey = "common.problem.privileged_helper_unavailable";

    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.processguardian"), "Process Guardian", "1.0.0", "🛡",
        "View and manage workloads supervised by the Guardian Agent",
        [AppPermissions.ServerGuardianRead, AppPermissions.ServerGuardianManage],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Guardian]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Label(string key) => ProcessGuardianLocalization.Resolve(language, key);
        var connection = context.ServerConnection;
        var host = context.GuardianHost;

        if (!context.HasAuthenticatedServer || !connection.IsCurrent || host is null)
        {
            ShowNotice("guardian.login_required");
            return;
        }

        var permissions = context.Permissions;
        if (permissions.GetStatus(AppPermissions.ServerGuardianRead) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerGuardianRead, cancellationToken);
        if (permissions.IsGranted(AppPermissions.ServerGuardianRead)
            && permissions.GetStatus(AppPermissions.ServerGuardianManage) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerGuardianManage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.IsCurrent || !permissions.IsGranted(AppPermissions.ServerGuardianRead))
        {
            ShowNotice(connection.IsCurrent ? "guardian.permission.read_required" : "guardian.login_required");
            return;
        }

        var views = new ProcessGuardianViews(Label);
        var model = new ProcessGuardianViewModel(context.Guardian, () => host.CurrentUsername,
            () => connection.Platform == HostPlatformKind.Windows, Label,
            code => IsHelperProblem(code) ? Label(HelperKey) : null,
            () => connection.IsCurrent && permissions.IsGranted(AppPermissions.ServerGuardianManage));
        var window = context.Windows.ShowWindow(Label(TitleKey), views.CreateWorkspace(model),
            new Rect(80, 60, 1240, 700), Manifest.IconGlyph);
        model.OpenComponentManagement = () => context.Activations.Activate(
            RelaxKonOSActivationUris.LaunchApplication(new AppId("relaxkonos.server-center")));
        model.SaveWithAdministratorApprovalAsync = definition => host.SaveWithAdministratorApprovalAsync(window, definition);
        model.ShowPrivilegedHelperUnavailableAsync = _ => window.ShowDialogAsync<bool>(
            Label(HelperKey + ".title"), dialog =>
            {
                var okay = new Button { Content = Label("common.ok"), Classes = { "primary" } };
                okay.Click += (_, _) => dialog.Close(true);
                return new StackPanel
                {
                    Margin = new Thickness(20), Spacing = 12,
                    Children = { new TextBlock { Text = Label(HelperKey), TextWrapping = TextWrapping.Wrap }, okay },
                };
            }, new Size(560, 280));

        model.ShowEditorAsync = async editing =>
        {
            try
            {
                await window.ShowDialogAsync<bool>(Label(editing ? "guardian.editor.edit_title" : "guardian.editor.create_title"), dialog =>
                {
                    model.CloseEditorAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return views.CreateEditor(model, () => dialog.Cancel(), async () =>
                    {
                        var path = await host.PickWorkloadPathAsync(window, Label("guardian.create.executable.select"), GuardianWorkloadPathKind.Executable);
                        if (path is not null) model.ExecutablePath = path;
                    }, async () =>
                    {
                        var path = await host.PickWorkloadPathAsync(window, Label("guardian.create.working_directory.select"), GuardianWorkloadPathKind.WorkingDirectory);
                        if (path is not null) model.WorkingDirectory = path;
                    });
                }, new Size(640, 650));
            }
            finally { model.CloseEditorAsync = null; }
        };

        model.ShowLogsAsync = workload =>
        {
            var logs = new GuardianLogWindowViewModel(context.Guardian, workload, Label);
            var logWindow = context.Windows.ShowWindow(logs.Title, views.CreateLogs(logs), new Rect(170, 120, 800, 520), Manifest.IconGlyph);
            void RefreshLogs(object? sender, SystemLanguageChangedEventArgs args) => Dispatch(logWindow.Closed, () =>
            {
                logWindow.Window.Title = logs.Title;
                logs.OnLanguageChanged(sender, args);
            });
            language.LanguageChanged += RefreshLogs;
            logWindow.Closed.Register(() =>
            {
                language.LanguageChanged -= RefreshLogs;
                Dispatcher.UIThread.Post(async () => await logs.DisposeAsync());
            });
            _ = logs.StartAsync();
            return Task.CompletedTask;
        };

        void RefreshWorkspace(object? sender, SystemLanguageChangedEventArgs args) => Dispatch(window.Closed, () =>
        {
            window.Window.Title = Label(TitleKey);
            model.OnLanguageChanged(sender, args);
        });
        language.LanguageChanged += RefreshWorkspace;
        var observation = connection.ObserveInvalidation(() => Dispatch(window.Closed, model.RefreshAccess));
        window.Closed.Register(() =>
        {
            language.LanguageChanged -= RefreshWorkspace;
            observation.Dispose();
        });
        await model.StartAsync();

        void ShowNotice(string key) => context.Windows.ShowWindow(Label(TitleKey),
            new TextBlock { Text = Label(key), Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap },
            new Rect(200, 160, 460, 180), Manifest.IconGlyph, false, false, false);
    }

    private static void Dispatch(CancellationToken closed, Action action)
    {
        void Apply() { if (!closed.IsCancellationRequested) action(); }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private static bool IsHelperProblem(string? problem) =>
        problem is not null && (problem.Equals("guardian.privileged_helper_unavailable", StringComparison.OrdinalIgnoreCase)
            || problem.Equals("privileged-helper-unavailable", StringComparison.OrdinalIgnoreCase)
            || problem.EndsWith("/privileged-helper-unavailable", StringComparison.OrdinalIgnoreCase));
}
