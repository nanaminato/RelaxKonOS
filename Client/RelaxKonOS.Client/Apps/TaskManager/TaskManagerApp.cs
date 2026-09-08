using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Apps.TaskManager.ViewModels;
using RelaxKonOS.Client.Apps.TaskManager.Views;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.TaskManager;

/// <summary>Built-in RemoteTaskManager — 远端宿主 OS 任务管理器。
/// 参考 Windows 任务管理器 / Linux 系统监视器：性能标签页（CPU/GPU/内存/磁盘/网络实时占用 + 柱状图）+
/// 进程标签页（当前可见进程列表，可结束任务，权限不足提示需在宿主 OS 提权）。
/// 数据经 <see cref="ITaskManagerClient"/> 调用 Server REST API（JWT via <see cref="IAuthSession"/>）；
/// 服务端以宿主 OS 进程身份采集（复用宿主用户/权限，不另建 ACL）。未登录时弹提示窗。</summary>
public sealed class TaskManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.taskmanager"),
        DisplayName: "任务管理器",
        Version: "1.0.0",
        IconGlyph: "📊",
        Description: "查看 CPU/内存/磁盘/网络/GPU 占用与进程，可结束任务",
        RequestedPermissions: [AppPermissions.ServerMetricsRead, AppPermissions.ServerProcessesManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(ITaskManagerClient)) as ITaskManagerClient;
        var performanceStream = context.Services.GetService(typeof(PerformanceStream)) as PerformanceStream;

        if (session is null || client is null || performanceStream is null || session.State != AuthSessionState.Authenticated)
        {
            var stub = new TextBlock
            {
                Text = LocalizedText.Get("task_manager.login_required"),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.taskmanager.display_name"), stub,
                bounds: new Rect(200, 160, 460, 180),
                iconGlyph: Manifest.IconGlyph,
                canResize: false, canMinimize: false, canMaximize: false);
            return;
        }

        var viewModel = new TaskManagerViewModel(client, performanceStream);
        var view = new TaskManagerMainView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.taskmanager.display_name"), view,
            bounds: new Rect(70, 55, 1080, 720),
            iconGlyph: Manifest.IconGlyph);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == RemoteKey.Letter('F') && e.Modifiers == RemoteKeyModifiers.Control)
            {
                view.FocusProcessFilter();
                e.Handled = true;
                return;
            }

            WindowShortcut.TryExecute(e, new RemoteKey("F5"), RemoteKeyModifiers.None, viewModel.RefreshCommand);
        };

        // 窗口打开后启动实时刷新
        _ = viewModel.StartAsync();
    }
}
