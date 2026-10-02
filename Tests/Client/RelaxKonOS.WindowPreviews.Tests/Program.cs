using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.Services.SystemUi;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;
using WindowState = RelaxKonOS.Core.Windows.WindowState;
using WindowRect = RelaxKonOS.Core.Primitives.Rect;

try
{
AppBuilder.Configure<PreviewTestApp>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
using var appearance = new AppearanceService(Application.Current!, new SystemStyleRegistry());
var settings = new ShellSettings(appearance);
var localization = new LocalizationService(settings, new SshDesktopSession(null!));
using var services = new ServiceCollection().AddSingleton(localization).BuildServiceProvider();
// The XAML localization extension resolves the host application's singleton provider.
typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);

var canvas = new Canvas { Width = 1200, Height = 800 };
var root = new Grid();
root.Children.Add(canvas);
var host = new Window { Width = 1200, Height = 800, Content = root };
host.Show();
Dispatcher.UIThread.RunJobs();
var manager = new WindowManagerService();
manager.Attach(canvas);
manager.SetHostBounds(new WindowRect(0, 0, 1200, 800));
var content = new Border { Background = Brushes.Crimson };
var first = manager.Create(new WindowCreateOptions(new AppId("preview.tests"), "Preview document", content,
    new WindowRect(30, 30, 1000, 700), IconGlyph: "□"));
var second = manager.Create(new WindowCreateOptions(new AppId("preview.tests"), "Second document",
    new Border { Background = Brushes.RoyalBlue }, new WindowRect(100, 80, 600, 400), IconGlyph: "□"));
Dispatcher.UIThread.RunJobs();

first.Thumbnail.Refresh(force: true);
var frame = first.Thumbnail.Image as Bitmap ?? throw new Exception("Visible window did not produce a thumbnail.");
Check(frame.PixelSize.Width <= 480 && frame.PixelSize.Height <= 300, "Capture dimensions are bounded.");
Check(ReadPixel(frame, frame.PixelSize.Width / 2, frame.PixelSize.Height / 2) == Color.Parse("Crimson"),
    "Thumbnail includes the window's rendered content, not a blank image.");
first.Thumbnail.Refresh();
Check(ReferenceEquals(frame, first.Thumbnail.Image), "Rapid consumers share the same frame.");
manager.Minimize(first);
var minimizedFrame = first.Thumbnail.Image;
Check(minimizedFrame is not null, "Minimizing caches the last visible frame.");
content.Background = Brushes.Lime;
first.Thumbnail.Refresh(force: true);
Check(ReferenceEquals(minimizedFrame, first.Thumbnail.Image), "Hidden windows retain their cache without capture or restoration.");
Check(first.State == WindowState.Minimized, "Capture does not restore a minimized window.");

using var overview = new WindowOverviewController(manager);
using var coordinator = new SystemUiCoordinator(manager, overview);
Check(coordinator.ShowOverview(), "Task view opens.");
Check(ReferenceEquals(overview.Items.Single(item => item.WindowId == first.Info.Id).Thumbnail, first.Thumbnail),
    "Task view shares the taskbar's thumbnail cache.");
var overviewView = new WindowOverviewView { DataContext = coordinator };
root.Children.Add(overviewView);
Dispatcher.UIThread.RunJobs();
var output = Path.Combine(AppContext.BaseDirectory, "preview-qa");
Directory.CreateDirectory(output);
using (var screenshot = new RenderTargetBitmap(new PixelSize(1200, 800)))
{
    screenshot.Render(overviewView);
    screenshot.Save(Path.Combine(output, "overview.png"), PngBitmapEncoderOptions.Default);
}
Check(coordinator.ActivateWindow(first.Info.Id), "A thumbnail restores and activates its window.");
Check(first.State == WindowState.Normal && first.IsActive && !overview.IsOverviewVisible,
    "Activation restores the minimized window and dismisses task view.");
Dispatcher.UIThread.RunJobs();
first.Thumbnail.Refresh(force: true);
frame = (Bitmap)first.Thumbnail.Image!;
Check(ReadPixel(frame, frame.PixelSize.Width / 2, frame.PixelSize.Height / 2) == Colors.Lime,
    "Restored windows refresh changed content.");
coordinator.ShowOverview();
manager.Minimize(second);
Check(coordinator.Cards.Single(card => card.WindowId == second.Info.Id).Item.IsMinimized,
    "Task view reflects changes to an inactive window without waiting for focus to change.");
manager.Restore(second);
coordinator.HideOverview();
manager.Focus(first);

root.Children.Remove(overviewView);
var previewState = new PreviewContext(manager);
using var taskbarPreviews = new WindowsTaskbarPreview(root, previewState);
var bar = new Grid { Height = 50, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Background = Brushes.WhiteSmoke };
var left = new Button { Content = "A", Width = 46, Height = 46, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Margin = new Thickness(80, 0, 0, 0) };
var right = new Button { Content = "B", Width = 46, Height = 46, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
bar.Children.Add(left);
bar.Children.Add(right);
root.Children.Add(bar);
root.Children.Add(taskbarPreviews.Host);
var single = new TaskbarGroupViewModel(first.Info.OwnerAppId, "First app", [first]);
var multiple = new TaskbarGroupViewModel(second.Info.OwnerAppId, "Second app", [first, second]);
taskbarPreviews.Register(left, single);
taskbarPreviews.Register(right, multiple);
Pump(30);
var leftPoint = left.TranslatePoint(new Point(20, 20), host)!.Value;
host.MouseMove(leftPoint);
Pump(120);
Check(previewState.OpenTaskbarGroup is null, "Passing briefly over an icon does not open a preview.");
Pump(350);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single), "Hover opens a single-window preview after the delay.");
var panel = taskbarPreviews.Host.Children.OfType<Border>().Single();
var panelPoint = panel.TranslatePoint(new Point(80, 70), host)!.Value;
host.MouseMove(panelPoint);
Pump(500);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single), "Moving from the icon into its preview keeps it open.");
var rightPoint = right.TranslatePoint(new Point(20, 20), host)!.Value;
host.MouseMove(rightPoint);
Pump(50);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, multiple), "Hovering another icon switches an open strip to that application's windows.");
Check(panel.Bounds.Right <= root.Bounds.Width && panel.Bounds.X >= 0, "Previews at the screen edge stay within the host.");
using (var screenshot = new RenderTargetBitmap(new PixelSize(1200, 800)))
{
    screenshot.Render(root);
    screenshot.Save(Path.Combine(output, "taskbar.png"), PngBitmapEncoderOptions.Default);
}
host.MouseMove(new Point(10, 10));
Pump(320);
Check(previewState.OpenTaskbarGroup is null, "Leaving the icon and strip dismisses after a short delay.");
host.MouseMove(leftPoint);
Pump(450);
host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
Check(previewState.OpenTaskbarGroup is null, "Escape dismisses the preview.");
host.MouseMove(new Point(10, 10));
host.MouseMove(leftPoint);
Pump(100);
taskbarPreviews.Dismiss();
Pump(450);
Check(previewState.OpenTaskbarGroup is null, "Deactivation cancels a pending hover without reopening the strip.");

manager.Focus(second);
previewState.ShowTaskbarPreview(multiple);
Pump(50);
var activateButton = panel.GetVisualDescendants().OfType<Button>().Single(button =>
    ReferenceEquals(button.Command, previewState.ActivateTaskbarWindowCommand) && ReferenceEquals(button.CommandParameter, first));
var activatePoint = activateButton.TranslatePoint(new Point(80, 60), host)!.Value;
host.MouseDown(activatePoint, MouseButton.Left);
host.MouseUp(activatePoint, MouseButton.Left);
Check(first.IsActive && previewState.OpenTaskbarGroup is null, "Clicking a taskbar thumbnail activates the window and dismisses the strip.");
var disposableWindow = manager.Create(new WindowCreateOptions(new AppId("preview.closable"), "Closable document",
    new Border { Background = Brushes.Gold }, new WindowRect(150, 130, 500, 350), IconGlyph: "□"));
multiple.Update([first, disposableWindow]);
previewState.ShowTaskbarPreview(multiple);
Pump(50);
var closeButton = panel.GetVisualDescendants().OfType<Button>().Single(button =>
    ReferenceEquals(button.Command, previewState.CloseTaskbarWindowCommand) && ReferenceEquals(button.CommandParameter, disposableWindow));
var closePoint = closeButton.TranslatePoint(new Point(15, 15), host)!.Value;
host.MouseDown(closePoint, MouseButton.Left);
host.MouseUp(closePoint, MouseButton.Left);
Check(manager.Windows.All(window => window.Info.Id != disposableWindow.Info.Id), "A preview close button closes only its own window.");
taskbarPreviews.Dismiss();

first.View.Content = new NativeControlHost();
Dispatcher.UIThread.RunJobs();
first.Thumbnail.Refresh(force: true);
Check(!first.Thumbnail.HasImage, "Native child surfaces use the icon/title presentation and clear stale images.");
coordinator.ShowOverview();
Check(coordinator.CloseWindow(second.Info.Id), "Task view can close a specific window.");
Check(!second.Thumbnail.HasImage, "Closing releases the frame cache.");
Check(overview.Items.All(item => item.WindowId != second.Info.Id), "Closed windows disappear from task view.");
manager.Close(first);
Check(!overview.IsOverviewVisible, "Closing the final window dismisses task view.");
host.Close();
Console.WriteLine($"PASS: window preview rendering, caching, native fallback, activation and close lifecycle. QA image: {output}");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.Exit(1);
}

static void Pump(int milliseconds)
{
    using var cancellation = new CancellationTokenSource(milliseconds);
    Dispatcher.UIThread.MainLoop(cancellation.Token);
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine($"PASS: {message}");
}

static Color ReadPixel(Bitmap bitmap, int x, int y)
{
    // Decode the encoded frame to normalize Skia's native BGRA/RGBA pixel format.
    using var stream = new MemoryStream();
    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    stream.Position = 0;
    using var decoded = SkiaSharp.SKBitmap.Decode(stream);
    var pixel = decoded.GetPixel(x, y);
    return Color.FromArgb(pixel.Alpha, pixel.Red, pixel.Green, pixel.Blue);
}

public sealed class PreviewTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://RelaxKonOS.UI/"))
            { Source = new Uri("avares://RelaxKonOS.UI/Themes/RelaxKonOSTheme.axaml") });
        Styles.Add(new StyleInclude(new Uri("avares://RelaxKonOS.WindowManager/"))
            { Source = new Uri("avares://RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml") });
    }
}

internal sealed class PreviewContext : ObservableObject, ITaskbarPreviewContext
{
    private TaskbarGroupViewModel? _group;
    public TaskbarGroupViewModel? OpenTaskbarGroup
    {
        get => _group;
        private set => SetProperty(ref _group, value);
    }
    public bool IsStartOpen => false;
    public ICommand ActivateTaskbarWindowCommand { get; }
    public ICommand CloseTaskbarWindowCommand { get; }
    public ICommand CloseTaskbarPreviewCommand { get; }

    public PreviewContext(WindowManagerService manager)
    {
        ActivateTaskbarWindowCommand = new RelayCommand<ManagedWindow>(window =>
        {
            if (window is null) return;
            if (window.State == WindowState.Minimized) manager.Restore(window);
            else manager.Focus(window);
            OpenTaskbarGroup = null;
        });
        CloseTaskbarWindowCommand = new RelayCommand<ManagedWindow>(window =>
        {
            if (window is not null) manager.Close(window);
        });
        CloseTaskbarPreviewCommand = new RelayCommand(() => OpenTaskbarGroup = null);
    }

    public void ShowTaskbarPreview(TaskbarGroupViewModel group) => OpenTaskbarGroup = group;
}
