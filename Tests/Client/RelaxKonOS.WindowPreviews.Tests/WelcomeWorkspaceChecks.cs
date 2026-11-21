using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RelaxKonOS.App.Welcome;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;

internal static class WelcomeWorkspaceChecks
{
    public static void Run(ShellSettings settings, ISystemLanguage language, IServiceProvider services)
    {
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        windows.Attach(new Canvas { Width = 1200, Height = 800 });
        var manager = new ApplicationManager(windows, services);
        var notebook = new LaunchProbe("relaxkonos.notepad");
        var installer = new LaunchProbe("relaxkonos.appinstaller");
        manager.Register(installer);
        var app = new WelcomeApp();
        var context = DispatchProxy.Create<IExternalAppContext, ImageViewerPackageFixture>();
        var fixture = (ImageViewerPackageFixture)(object)context;
        fixture.Initialize(windows, language, app.Manifest.Id);
        fixture.Activations = new ActivationProbe(manager, app.Manifest.Id);

        settings.Language = "zh-CN";
        app.ActivateAsync(context).GetAwaiter().GetResult();
        var window = windows.Windows.Single();
        var view = (WelcomeView)window.View.Content!;
        var button = view.FindControl<Button>("OpenNotebookButton")!;
        foreach (var (culture, title, label) in new[]
        {
            ("zh-CN", "欢迎", "打开记事本"),
            ("en-US", "Welcome", "Open Notebook"),
            ("ja-JP", "ようこそ", "メモ帳を開く"),
        })
        {
            settings.Language = culture;
            Dispatcher.UIThread.RunJobs();
            Check(window.Title == title && Equals(button.Content, label), $"Welcome did not localize to {culture}.");
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(installer.Launches == 1, "Missing Notebook should open the application installer.");
        manager.Register(notebook);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(notebook.Launches == 1 && installer.Launches == 1, "Welcome should open the registered Notebook.");
        var invalid = manager.Activate(new(new Uri("relaxkonos://applications/relaxkonos.notepad?other=1")));
        Check(invalid.Status == AppActivationStatus.InvalidUri && notebook.Launches == 1, "A launch route cannot carry query parameters.");

        windows.Close(window);
        settings.Language = "en-US";
        Dispatcher.UIThread.RunJobs();
        Check(window.Title == "ようこそ", "The closed Welcome window must stop following language changes.");
        Console.WriteLine("PASS: external Welcome uses three languages, releases its language subscription and opens Notebook or App Installer.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class LaunchProbe(string id) : RemoteApplicationBase
    {
        public int Launches { get; private set; }
        public override ApplicationManifest Manifest { get; } = new(new AppId(id), id);
        public override void Activate(RelaxKonOS.AppSDK.AppContext context) => Launches++;
    }

    private sealed class ActivationProbe(ApplicationManager manager, AppId source) : IAppActivation
    {
        public AppActivationResult Activate(Uri uri, bool userInitiated = true, string? correlationId = null) =>
            manager.Activate(new(uri, source, userInitiated, correlationId));
    }
}
