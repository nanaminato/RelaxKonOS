using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.App.ImageViewer;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.ImageViewer;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Examples.ImageViewerPilot;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using Rect = RelaxKonOS.Core.Primitives.Rect;

internal static class ImageViewerWorkspaceChecks
{
    internal static readonly byte[] Svg = Encoding.UTF8.GetBytes("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 60"><rect width="120" height="60" fill="red"/></svg>
        """);

    public static void Run(ShellSettings settings, LocalizationService localization)
    {
        var originalLanguage = settings.Language;
        var explorer = DispatchProxy.Create<IExplorerClient, ImageViewerExplorerFixture>();
        using var services = new ServiceCollection().AddSingleton(localization).AddSingleton(explorer).BuildServiceProvider();
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        var canvas = new Canvas();
        var host = new Window { Width = 1100, Height = 800, Content = canvas };
        try
        {
            settings.Language = "en-US";
            host.Show();
            manager.Attach(canvas);
            manager.SetHostBounds(new Rect(0, 0, 1100, 800));
            var apps = new ApplicationManager(manager, services);
            var builtIn = new ImageViewerApp();
            apps.RegisterBuiltIn(builtIn);
            var id = new AppId("relaxkonos.imageviewer");
            Check(apps.IsBuiltIn(id) && apps.SupportsFile(id, "/test/photo.JFIF"), "The built-in registry lost the official identity or file associations.");
            Check(apps.OpenFile(id, "/test/logo.SVG"), "The built-in file-open route was rejected.");
            var builtInWindow = manager.Windows.Single();
            var builtInView = CheckViewer(builtInWindow);
            Check(((ImageViewerExplorerFixture)(object)explorer).Path == "/test/logo.SVG", "The built-in entry did not use the Explorer file source.");
            var model = (ImageViewerViewModel)builtInView.DataContext!;

            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            using var icon = AppIconImageLoader.Load("avares://RelaxKonOS.Client/Assets/AppIcons/imageviewer.png") as Bitmap;
            Check(icon is not null, "The launcher could not find the shipped Image Viewer icon.");
            KeyPress(builtInWindow, Key.D0, KeyModifiers.Control);
            Check(model.ZoomPercent == 100, "Ctrl+0 did not reset to actual size.");
            KeyPress(builtInWindow, Key.OemPlus, KeyModifiers.Control);
            Check(model.ZoomPercent == 125, "Ctrl++ did not zoom in.");
            KeyPress(builtInWindow, Key.OemMinus, KeyModifiers.Control);
            Check(model.ZoomPercent == 100, "Ctrl+- did not zoom out.");
            KeyPress(builtInWindow, Key.F, KeyModifiers.None);
            Check(model.ZoomPercent > 100 && model.DisplayWidth > 120, "F did not fit the image to the viewer.");
            settings.Language = "ja-JP";
            Dispatcher.UIThread.RunJobs();
            Check(builtInWindow.Title == "画像ビューアー" && model.ZoomInText == "拡大", "The open window did not follow the workspace language.");
            manager.Close(builtInWindow);
            Check(!model.HasImage, "Closing the built-in window retained the image.");

            using var sshModel = new ImageViewerViewModel(localization, null, isSshSession: true);
            Check(sshModel.SessionText == "SSH デスクトップ", "The SSH session label was lost.");
            var context = DispatchProxy.Create<IExternalAppContext, ImageViewerPackageFixture>();
            var fixture = (ImageViewerPackageFixture)(object)context;
            var pilot = new ImageViewerPilotApp();
            fixture.Initialize(manager, localization, pilot.Manifest.Id);
            Check(pilot.Manifest.Id != id && pilot.Manifest.FileExtensions.SequenceEqual(builtIn.Manifest.FileExtensions),
                "The pilot should retain its external id and advertise the same image formats.");
            pilot.OpenFileAsync(context, "/test/pilot.svg").GetAwaiter().GetResult();
            var pilotWindow = manager.Windows.Single();
            var pilotView = CheckViewer(pilotWindow);
            Check(pilotView.GetType() == builtInView.GetType() && fixture.ReadPath == "/test/pilot.svg"
                && pilotWindow.Title == "pilot.svg · Image Viewer Pilot",
                "The pilot did not render the shared viewer using SDK server files.");
            manager.Close(pilotWindow);
            Check(pilotView.DataContext is ImageViewerViewModel { HasImage: false }, "Closing the package window retained the image.");

            fixture.DenyReads = true;
            settings.Language = "zh-CN";
            Dispatcher.UIThread.RunJobs();
            pilot.OpenFileAsync(context, "/test/denied.svg").GetAwaiter().GetResult();
            var denied = (ImageViewerViewModel)((ImageViewerView)manager.Windows.Single().View.Content!).DataContext!;
            Check(!denied.HasImage && denied.StatusText == "请在设置中允许此应用读取服务器文件。", "The pilot did not explain the denied capability in the workspace language.");
            Console.WriteLine("PASS IMAGE VIEWER: official identity, shared SVG view and icon, shortcuts, live localization and SDK file permissions.");
        }
        finally
        {
            foreach (var window in manager.Windows.ToArray()) manager.ForceClose(window);
            host.Close();
            settings.Language = originalLanguage;
        }
    }

    private static ImageViewerView CheckViewer(ManagedWindow window)
    {
        var view = (ImageViewerView)window.View.Content!;
        Check(view.DataContext is ImageViewerViewModel { HasImage: true, PixelWidth: 120, PixelHeight: 60, DisplayWidth: > 0 },
            "The shared viewer did not decode or size the SVG.");
        Check(window.View.HeaderLeading is Image { Source: not null } && window.View.HeaderTrailing is StackPanel,
            "The shared view did not attach its icon and actions to the managed title bar.");
        return view;
    }

    private static void KeyPress(ManagedWindow window, Key key, KeyModifiers modifiers) =>
        window.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class ImageViewerExplorerFixture : DispatchProxy
{
    public string? Path { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name != nameof(IExplorerClient.ReadFileAsync)) throw new NotSupportedException(method?.Name);
        Path = (string)args![0]!;
        return Task.FromResult<byte[]?>(ImageViewerWorkspaceChecks.Svg);
    }
}

public class ImageViewerPackageFixture : DispatchProxy, IServerFiles, IExternalAppWindowService
{
    private IWindowManager _manager = null!;
    private ISystemLanguage _language = null!;
    private AppId _id;
    public string? ReadPath { get; private set; }
    public bool DenyReads { get; set; }

    internal void Initialize(IWindowManager manager, ISystemLanguage language, AppId id)
    {
        _manager = manager;
        _language = language;
        _id = id;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_ServerFiles" or "get_Windows" => this,
        "get_SystemLanguage" => _language,
        _ => throw new NotSupportedException(method?.Name),
    };

    public Task<ServerFileReadResult> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ReadPath = path;
        return Task.FromResult(DenyReads
            ? new ServerFileReadResult(AppCapabilityResult.PermissionDenied, null, null)
            : new ServerFileReadResult(AppCapabilityResult.Succeeded, new MemoryStream(ImageViewerWorkspaceChecks.Svg), "pilot.svg"));
    }

    public IExternalAppWindowHandle ShowWindow(string title, Control content, Rect? bounds = null, string? iconGlyph = null,
        bool canResize = true, bool canMinimize = true, bool canMaximize = true) =>
        new WindowHandle(_manager, _manager.Create(new WindowCreateOptions(_id, title, content, bounds)));

    private sealed class WindowHandle : IExternalAppWindowHandle
    {
        private readonly CancellationTokenSource _closed = new();
        public WindowHandle(IWindowManager manager, ManagedWindow window)
        {
            Window = window;
            manager.WindowClosed += Close;
            void Close(object? sender, ManagedWindow closed)
            {
                if (closed != window) return;
                manager.WindowClosed -= Close;
                _closed.Cancel();
                _closed.Dispose();
            }
        }
        public ManagedWindow Window { get; }
        public CancellationToken Closed => _closed.Token;
        public bool IsFullScreen => Window.IsFullScreen;
        public void EnterFullScreen() => throw new NotSupportedException();
        public void ExitFullScreen() => throw new NotSupportedException();
    }
}
