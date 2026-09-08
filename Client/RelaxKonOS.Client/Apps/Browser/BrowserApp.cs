using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Apps.Browser.ViewModels;
using RelaxKonOS.Client.Apps.Browser.Views;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.Browser;

/// <summary>Built-in RemoteBrowser — 平台原生 WebView 嵌入式浏览器。
/// UI 在 Client 本地渲染（NativeWebView 用平台原生引擎：Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），
/// 网页内容走客户端网络；Server 仅持久化书签与历史记录（按用户隔离，JWT via IAuthSession）。
/// 未登录时弹提示窗。</summary>
public sealed class BrowserApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.browser"),
        DisplayName: "RemoteBrowser",
        Version: "1.0.0",
        IconGlyph: "🌐",
        Description: "网页浏览器（书签 / 历史记录持久化到服务端）");

    public override void Activate(AppContext context)
    {
        BrowserDiagnostics.EnsureUiWatchdog();
        BrowserDiagnostics.Record("Browser activation requested.");
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IBrowserClient)) as IBrowserClient;

        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            var stub = new TextBlock
            {
                Text = LocalizedText.Get("browser.login_required"),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
            context.ShowWindow("RemoteBrowser", stub,
                bounds: new Rect(200, 160, 460, 180),
                iconGlyph: Manifest.IconGlyph,
                canResize: false, canMinimize: false, canMaximize: false);
            return;
        }

        var viewModel = new BrowserViewModel(client);
        var view = new BrowserMainView { DataContext = viewModel };
        BrowserDiagnostics.Record("Browser view and view-model created; opening managed window.");
        var window = context.ShowWindow("RemoteBrowser", view,
            bounds: new Rect(60, 50, 1100, 720),
            iconGlyph: Manifest.IconGlyph);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == RemoteKey.Letter('L') && e.Modifiers == RemoteKeyModifiers.Control)
            {
                view.FocusAddressBox();
                e.Handled = true;
                return;
            }

            if (WindowShortcut.TryExecute(e, RemoteKey.Left, RemoteKeyModifiers.Alt, viewModel.GoBackCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Right, RemoteKeyModifiers.Alt, viewModel.GoForwardCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('R'), RemoteKeyModifiers.Control, viewModel.RefreshCommand)
                || WindowShortcut.TryExecute(e, new RemoteKey("F5"), RemoteKeyModifiers.None, viewModel.RefreshCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('D'), RemoteKeyModifiers.Control, viewModel.ToggleBookmarkCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('H'), RemoteKeyModifiers.Control, viewModel.SwitchToHistoryCommand)
                || WindowShortcut.TryExecute(e, new RemoteKey("F11"), RemoteKeyModifiers.None, viewModel.ToggleFullScreenCommand))
                return;

            if (e.Key != RemoteKey.Escape || !window.IsFullScreen)
                return;

            context.ExitFullScreen(window);
            e.Handled = true;
        };
        viewModel.CloseAction = () => Dispatcher.UIThread.Post(() =>
        {
            view.ClosePlatformBrowser();
            context.WindowManager.Close(window);
        });
        viewModel.ToggleFullScreenAction = () =>
        {
            if (window.IsFullScreen)
                context.ExitFullScreen(window);
            else
                context.EnterFullScreen(window);
        };
        viewModel.RequestSettingsAsync = async () =>
        {
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("browser.settings.title"), dialog =>
            {
                viewModel.CloseSettingsAction = () => dialog.Close(true);
                return new BrowserSettingsView { DataContext = viewModel };
            }, new RelaxKonOS.Core.Primitives.Size(520, 320));
        };

        // NativeWebView is a platform child view and does not participate in Avalonia's
        // normal ZIndex composition. Hide it while another managed window is active.
        window.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ManagedWindow.IsActive) || e.PropertyName == nameof(ManagedWindow.State))
                view.SetWebViewVisible(window.IsActive && window.IsOnScreen);
            if (e.PropertyName == nameof(ManagedWindow.State))
                viewModel.IsFullScreen = window.IsFullScreen;
        };
        window.FocusRequested += (_, _) => view.SetWebViewVisible(true);
        // NativeWebView is an operating-system child window. Moving/resizing it for every
        // pointer event is especially expensive on WebKitGTK and can deadlock the UI/native
        // render loops. Keep the lightweight Avalonia chrome interactive and restore the
        // native surface once the bounds have settled.
        window.View.BoundsInteractionStarted += (_, _) => view.SetWebViewVisible(false);
        window.View.BoundsInteractionCompleted += (_, _) =>
            view.SetWebViewVisible(window.IsActive && window.IsOnScreen);
        view.SetWebViewVisible(window.IsActive && window.IsOnScreen);
        BrowserDiagnostics.Record("Browser managed window opened.");

        // 窗口打开后异步加载书签 + 历史
        _ = viewModel.LoadAsync();
    }
}
