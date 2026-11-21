using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.App.TaskManager.ViewModels;
using RelaxKonOS.App.TaskManager.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.App.TaskManager;

public sealed class TaskManagerPackageApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.taskmanager.display_name";

    public ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.taskmanager"),
        DisplayName: "Task Manager",
        Version: "1.0.0",
        IconGlyph: "📊",
        Description: "View CPU, memory, disk, network and GPU usage and the process list; end tasks here",
        RequestedPermissions: [AppPermissions.ServerMetricsRead, AppPermissions.ServerProcessesManage],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Metrics, ServerCapabilities.Processes]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Label(string key) => TaskManagerLocalization.Resolve(language, key);

        if (!context.HasAuthenticatedServer)
        {
            var message = new TextBlock
            {
                Text = Label("task_manager.login_required"),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
            var signIn = context.Windows.ShowWindow(Label(TitleKey), message, new Rect(200, 160, 460, 180),
                Manifest.IconGlyph, canResize: false, canMinimize: false, canMaximize: false);
            ObserveLanguage(signIn, language, () =>
            {
                signIn.Window.Title = Label(TitleKey);
                message.Text = Label("task_manager.login_required");
            });
            return Task.CompletedTask;
        }

        var model = new TaskManagerViewModel(context.ServerProcesses, context.ServerMonitor, context.ServerConnection, language);
        var view = new TaskManagerMainView { DataContext = model };
        var owner = context.Windows.ShowWindow(Label(TitleKey), view, new Rect(70, 55, 1080, 720), Manifest.IconGlyph);
        view.AttachWindowHeader(owner.Window);
        ObserveLanguage(owner, language, () => owner.Window.Title = Label(TitleKey));
        owner.Closed.Register(model.Stop);
        owner.Window.KeyDown += (_, e) =>
        {
            if (e.Key == RemoteKey.Letter('F') && e.Modifiers == RemoteKeyModifiers.Control)
            {
                view.FocusProcessFilter();
                e.Handled = true;
                return;
            }
            WindowShortcut.TryExecute(e, new RemoteKey("F5"), RemoteKeyModifiers.None, model.RefreshCommand);
        };
        _ = model.StartAsync(context.Permissions);
        return Task.CompletedTask;
    }

    private static void ObserveLanguage(IExternalAppWindowHandle owner, ISystemLanguage language, Action refresh)
    {
        var closed = owner.Closed;
        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
        {
            void Apply()
            {
                if (!closed.IsCancellationRequested) refresh();
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        language.LanguageChanged += LanguageChanged;
        closed.Register(() => language.LanguageChanged -= LanguageChanged);
    }
}
