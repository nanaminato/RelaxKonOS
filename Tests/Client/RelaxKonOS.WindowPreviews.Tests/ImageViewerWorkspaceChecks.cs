using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using RelaxKonOS.App.ImageViewer;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Examples.ImageViewerPilot;
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
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        var canvas = new Canvas();
        var host = new Window { Width = 1100, Height = 800, Content = canvas };
        try
        {
            settings.Language = "en-US";
            host.Show();
            manager.Attach(canvas);
            manager.SetHostBounds(new Rect(0, 0, 1100, 800));
            var context = DispatchProxy.Create<IExternalAppContext, ImageViewerPackageFixture>();
            var fixture = (ImageViewerPackageFixture)(object)context;
            fixture.ReadPermission = AppPermissionStatus.Undecided;
            var official = new ImageViewerApp();
            fixture.Initialize(manager, localization, official.Manifest.Id);
            Check(official.Manifest.SupportsFile("/test/photo.JFIF"), "Image Viewer lost its file associations.");
            official.OpenFileAsync(context, "/test/logo.SVG").GetAwaiter().GetResult();
            var officialWindow = manager.Windows.Single();
            var officialView = CheckViewer(officialWindow);
            Check(fixture.PermissionRequests == 1 && fixture.ReadPath == "/test/logo.SVG",
                "The official entry did not request permission before reading via the SDK.");
            var model = (ImageViewerViewModel)officialView.DataContext!;

            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            KeyPress(officialWindow, Key.D0, KeyModifiers.Control);
            Check(model.ZoomPercent == 100, "Ctrl+0 did not reset to actual size.");
            KeyPress(officialWindow, Key.OemPlus, KeyModifiers.Control);
            Check(model.ZoomPercent == 125, "Ctrl++ did not zoom in.");
            KeyPress(officialWindow, Key.OemMinus, KeyModifiers.Control);
            Check(model.ZoomPercent == 100, "Ctrl+- did not zoom out.");
            KeyPress(officialWindow, Key.F, KeyModifiers.None);
            Check(model.ZoomPercent > 100 && model.DisplayWidth > 120, "F did not fit the image to the viewer.");
            settings.Language = "ja-JP";
            Dispatcher.UIThread.RunJobs();
            Check(officialWindow.Title == "画像ビューアー" && model.ZoomInText == "拡大", "The open window did not follow the workspace language.");
            manager.Close(officialWindow);
            Check(!model.HasImage, "Closing the official window retained the image.");

            fixture.IsSshSession = true;
            var pilot = new ImageViewerPilotApp();
            fixture.Initialize(manager, localization, pilot.Manifest.Id);
            Check(pilot.Manifest.Id != official.Manifest.Id && pilot.Manifest.FileExtensions.SequenceEqual(official.Manifest.FileExtensions),
                "The pilot should retain its external id and advertise the same image formats.");
            pilot.OpenFileAsync(context, "/test/pilot.svg").GetAwaiter().GetResult();
            var pilotWindow = manager.Windows.Single();
            var pilotView = CheckViewer(pilotWindow);
            Check(pilotView.GetType() == officialView.GetType() && fixture.ReadPath == "/test/pilot.svg"
                && ((ImageViewerViewModel)pilotView.DataContext!).SessionText.StartsWith("SSH ", StringComparison.Ordinal)
                && pilotWindow.Title == "pilot.svg · Image Viewer Pilot",
                "The pilot did not render the shared viewer using SDK server files.");
            manager.Close(pilotWindow);
            Check(pilotView.DataContext is ImageViewerViewModel { HasImage: false }, "Closing the package window retained the image.");

            fixture.ReadPermission = AppPermissionStatus.Denied;
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

public class ImageViewerPackageFixture : DispatchProxy, IServerFiles, IExternalAppWindowService, IAppPermissionScope
{
    private IWindowManager _manager = null!;
    private ISystemLanguage _language = null!;
    private AppId _id;
    public string? ReadPath { get; private set; }
    public AppPermissionStatus ReadPermission { get; set; } = AppPermissionStatus.Granted;
    public int PermissionRequests { get; private set; }
    public bool IsSshSession { get; set; }
    public IAppActivation? Activations { get; set; }

    internal void Initialize(IWindowManager manager, ISystemLanguage language, AppId id)
    {
        _manager = manager;
        _language = language;
        _id = id;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_ServerFiles" or "get_Windows" or "get_Permissions" => this,
        "get_SystemLanguage" => _language,
        "get_Activations" when Activations is not null => Activations,
        _ => throw new NotSupportedException(method?.Name),
    };

    public Task<ServerFileReadResult> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ReadPath = path;
        return Task.FromResult(ReadPermission != AppPermissionStatus.Granted
            ? new ServerFileReadResult(AppCapabilityResult.PermissionDenied, null, null)
            : new ServerFileReadResult(AppCapabilityResult.Succeeded, new MemoryStream(ImageViewerWorkspaceChecks.Svg), "pilot.svg"));
    }

    public Task<AppCapabilityResult> WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<ServerFileContentResult> ReadAsync(string path, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<ServerFilePickResult> PickFileAsync(IExternalAppWindowHandle owner, ServerFilePickerOptions options)
        => throw new NotSupportedException();

    public AppPermissionStatus GetStatus(string permissionId) => ReadPermission;
    public bool IsGranted(string permissionId) => ReadPermission == AppPermissionStatus.Granted;
    public Task<AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default)
    {
        PermissionRequests++;
        ReadPermission = AppPermissionStatus.Granted;
        return Task.FromResult(ReadPermission);
    }
    public Task OpenSettingsAsync() => Task.CompletedTask;

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
        public Task<TResult?> ShowDialogAsync<TResult>(string title, Func<ModalDialog<TResult>, Control> content, RelaxKonOS.Core.Primitives.Size preferredSize)
            => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => throw new NotSupportedException();
    }
}
