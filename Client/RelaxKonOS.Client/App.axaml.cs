using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.ViewModels.Login;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Client.Views;
using RelaxKonOS.Client.Views.Login;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client;

public partial class App : Application
{
    /// <summary>Root DI container for the RelaxKonOS client shell.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        // The catalog repair writes descriptor files asynchronously.  Keep the UI thread free
        // while it runs; synchronously waiting here deadlocks its continuation before the login
        // window can be created.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime startupDesktop)
            startupDesktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Services = await Bootstrapper.BuildAsync(this);
        // Install the sole palette source before the first (login) window is created.
        _ = Services.GetRequiredService<RelaxKonOS.Client.Services.Theming.ThemeService>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 启动分叉（mstsc 风格）：先弹独立登录窗，登录成功后再进入桌面。
            // OnExplicitShutdown 防止登录窗→桌面切换时进程提前退出。
            var session = Services.GetRequiredService<IAuthSession>();
            var notificationPreferences = Services.GetRequiredService<LoginNotificationPreferenceStore>();
            var loginViewModel = Services.GetRequiredService<LoginViewModel>();
            LoginWindow CreateLoginWindow() => new()
            {
                DataContext = loginViewModel,
            };
            var loginWindow = CreateLoginWindow();
            MainWindow? mainWindow = null;
            var replacingMainWindow = false;
            desktop.MainWindow = loginWindow;
            loginWindow.Show();

            session.StateChanged += (_, e) =>
            {
                // 切换到桌面必须在 UI 线程执行。
                Dispatcher.UIThread.Post(async () =>
                {
                    if (e.State == AuthSessionState.Unauthenticated
                        && e.EndReason == AuthSessionEndReason.RefreshTokenInvalid
                        && mainWindow is not null)
                    {
                        replacingMainWindow = true;
                        mainWindow.Close();
                        mainWindow = null;
                        loginWindow = CreateLoginWindow();
                        desktop.MainWindow = loginWindow;
                        loginWindow.Show();
                        await loginViewModel.LoadSavedProfilesAsync();
                        loginViewModel.ShowSessionExpiredMessage();
                        replacingMainWindow = false;
                        return;
                    }

                    if (e.State != AuthSessionState.Authenticated || mainWindow is not null)
                        return;
                    if (e.RememberedProfileSaveResult is { } saveResult
                        && saveResult != RememberedProfileSaveResult.Saved
                        && !notificationPreferences.IsPasswordSaveWarningDismissed())
                        await ShowRememberedProfileSaveWarningAsync(loginWindow, saveResult, notificationPreferences);

                    var shell = Services.GetRequiredService<DesktopShellViewModel>();
                    mainWindow = new MainWindow { DataContext = shell };
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    loginWindow.Close();
                    mainWindow.Closed += (_, _) =>
                    {
                        if (!replacingMainWindow)
                            desktop.Shutdown();
                    };
                });
            };

            // Keep the login window visible so the user can choose one of several remembered servers.
            // Selecting an entry with a saved password logs in without asking for it.
            _ = loginViewModel.LoadSavedProfilesAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowRememberedProfileSaveWarningAsync(
        Window owner, RememberedProfileSaveResult saveResult,
        LoginNotificationPreferenceStore notificationPreferences)
    {
        var localization = Services.GetRequiredService<LoginLocalizationService>();
        var messageKey = saveResult == RememberedProfileSaveResult.SecureStorageUnavailable
            ? "login.password_save.secure_storage_unavailable"
            : "login.password_save.local_storage_failed";
        var fallback = saveResult == RememberedProfileSaveResult.SecureStorageUnavailable
            ? "You signed in, but the password could not be saved securely. The server and username were saved. " +
              "On Linux, start or unlock a Secret Service provider such as GNOME Keyring or KWallet, then sign in again."
            : "You signed in, but RelaxKonOS could not save this remembered connection. Check that the system credential store and local data directory are available, then sign in again.";

        var dialog = new Window
        {
            Title = localization.Get("login.password_save.title", "Password not saved"),
            Width = 500,
            MinHeight = 190,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var acknowledge = new Button
        {
            Content = localization.Get("common.ok", "OK"),
            MinWidth = 88,
            Padding = new Thickness(16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var doNotShowAgain = new CheckBox
        {
            Content = localization.Get("login.password_save.do_not_show_again", "Don't show this again"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        acknowledge.Click += (_, _) =>
        {
            if (doNotShowAgain.IsChecked == true)
                notificationPreferences.DismissPasswordSaveWarning();
            dialog.Close();
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = localization.Get(messageKey, fallback),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                },
                doNotShowAgain,
                acknowledge,
            },
        };
        await dialog.ShowDialog(owner);
    }
}
