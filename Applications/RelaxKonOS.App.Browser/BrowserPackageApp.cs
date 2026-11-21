using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.App.Browser.ViewModels;
using RelaxKonOS.App.Browser.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.App.Browser;

/// <summary>Built-in RemoteBrowser — 平台原生 WebView 嵌入式浏览器。
/// UI 在 Client 本地渲染（NativeWebView 用平台原生引擎：Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），
/// 网页内容走客户端网络；Server 仅持久化书签与历史记录（按用户隔离，JWT via IAuthSession）。
/// 未登录时弹提示窗。</summary>
public sealed class BrowserPackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.browser"),
        DisplayName: "RemoteBrowser",
        Version: "1.0.0",
        IconGlyph: "🌐",
        Description: "Web browser (bookmarks and history are stored on the server)");

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BrowserDiagnostics.EnsureUiWatchdog();
        BrowserDiagnostics.Record("Browser activation requested.");
        var language = context.SystemLanguage;
        string Label(string key) => BrowserLocalization.Resolve(language, key);
        var client = context.BrowserData;
        if (client is null || !context.HasAuthenticatedServer)
        {
            var stub = new TextBlock
            {
                Text = Label("browser.login_required"),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
            var loginWindow = context.Windows.ShowWindow(Label("application.relaxkonos.browser.display_name"), stub,
                bounds: new Rect(200, 160, 460, 180),
                iconGlyph: Manifest.IconGlyph,
                canResize: false, canMinimize: false, canMaximize: false);
            FollowTitle(loginWindow, language);
            return Task.CompletedTask;
        }

        var viewModel = new BrowserViewModel(client, language, BrowserDiagnostics.Record);
        var view = new BrowserMainView { DataContext = viewModel };
        BrowserDiagnostics.Record("Browser view and view-model created; opening managed window.");
        var owner = context.Windows.ShowWindow(Label("application.relaxkonos.browser.display_name"), view,
            bounds: new Rect(60, 50, 1100, 720),
            iconGlyph: Manifest.IconGlyph);
        var window = owner.Window;
        view.AttachWindowHeader(window);
        FollowTitle(owner, language);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == RemoteKey.Letter('T') && e.Modifiers == RemoteKeyModifiers.Control)
            {
                viewModel.AddTabCommand.Execute(null);
                view.FocusAddressBox();
                e.Handled = true;
                return;
            }
            if (e.Key == RemoteKey.Letter('W') && e.Modifiers == RemoteKeyModifiers.Control)
            {
                viewModel.CloseSelectedTab();
                e.Handled = true;
                return;
            }
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

            owner.ExitFullScreen();
            e.Handled = true;
        };
        viewModel.CloseAction = () => Dispatcher.UIThread.Post(() =>
        {
            view.ClosePlatformBrowser();
            window.CloseCommand.Execute(null);
        });
        viewModel.ToggleFullScreenAction = () =>
        {
            if (window.IsFullScreen)
                owner.ExitFullScreen();
            else
                owner.EnterFullScreen();
        };
        viewModel.RequestSettingsAsync = async () =>
        {
            view.SetWebViewVisible(false);
            try
            {
                await owner.ShowDialogAsync<bool>(Label("browser.settings.title"), dialog =>
                {
                    viewModel.CloseSettingsAction = () => dialog.Close(true);
                    return new BrowserSettingsView { DataContext = viewModel };
                }, new RelaxKonOS.Core.Primitives.Size(520, 320));
            }
            finally { view.SetWebViewVisible(window.IsActive && window.IsOnScreen); }
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

        owner.Closed.Register(view.ClosePlatformBrowser);
        _ = viewModel.LoadAsync();
        return Task.CompletedTask;
    }

    private static void FollowTitle(IExternalAppWindowHandle owner, ISystemLanguage language)
    {
        void Refresh(object? sender, SystemLanguageChangedEventArgs args)
        {
            void Apply()
            {
                if (!owner.Closed.IsCancellationRequested)
                    owner.Window.Title = BrowserLocalization.Resolve(language, "application.relaxkonos.browser.display_name");
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        language.LanguageChanged += Refresh;
        owner.Closed.Register(() => language.LanguageChanged -= Refresh);
    }
}
