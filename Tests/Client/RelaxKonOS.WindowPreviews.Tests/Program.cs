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
using System.Text.Json;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.Services.SystemUi;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Runtime;
using RelaxKonOS.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;
using WindowState = RelaxKonOS.Core.Windows.WindowState;
using WindowRect = RelaxKonOS.Core.Primitives.Rect;

try
{
LanguageSwitchDiagnostics.Initialize(Path.Combine(AppContext.BaseDirectory, "preview-qa", "logs"));
TaskbarPreviewDiagnostics.Initialize(Path.Combine(AppContext.BaseDirectory, "preview-qa", "logs"));
AppBuilder.Configure<PreviewTestApp>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
using var appearance = new AppearanceService(Application.Current!, new SystemStyleRegistry());
var settings = new ShellSettings(appearance);
var localization = new LocalizationService(settings, new SshDesktopSession(null!));
using var services = new ServiceCollection().AddSingleton(localization).BuildServiceProvider();
// The XAML localization extension resolves the host application's singleton provider.
typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);
SettingsInteractionChecks.Run(settings, localization);
SettingsWindowChecks.Run(settings, localization);
DesktopDeviceSettingsChecks.Run(settings, appearance);
HostTimeCompletionChecks.Run(localization);
HostIdentityCompletionChecks.Run(localization);
WorkspaceEnvironmentEditorChecks.Run(settings);
if (args.Contains("--settings-interaction-only")) return;
DesktopDisconnectChecks.Run(settings);

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
PointerEventArgs? lastIconExit = null;
PointerEventArgs? lastRightExit = null;
left.PointerExited += (_, args) => lastIconExit = args;
right.PointerExited += (_, args) => lastRightExit = args;
Pump(30);
var leftPoint = left.TranslatePoint(new Point(20, 20), host)!.Value;
host.MouseMove(leftPoint);
Pump(120);
Check(previewState.OpenTaskbarGroup is null, "Passing briefly over an icon does not open a preview.");
Pump(350);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single), "Hover opens a single-window preview after the delay.");
var panel = taskbarPreviews.Host.Children.OfType<Border>().Single(border => border.Child is ScrollViewer);
PointerEventArgs? lastPanelExit = null;
panel.PointerExited += (_, args) => lastPanelExit = args;
var gapPoint = left.TranslatePoint(new Point(20, -3), host)!.Value;
host.MouseMove(gapPoint);
Pump(320);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single),
    "Crossing slowly through the gap between the icon and its preview does not dismiss the strip.");
var panelPoint = panel.TranslatePoint(new Point(80, 70), host)!.Value;
host.MouseMove(panelPoint);
Pump(500);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single), "Moving from the icon into its preview keeps it open.");
foreach (var localPoint in new[] { new Point(20, 20), new Point(180, 20), new Point(80, 120), new Point(4, 80) })
{
    host.MouseMove(panel.TranslatePoint(localPoint, host)!.Value);
    Pump(320);
    Check(ReferenceEquals(previewState.OpenTaskbarGroup, single),
        "Moving between preview title, controls, image and padding keeps the strip open.");
}
// Model an icon's leave notification arriving after the panel has already received enter.
left.RaiseEvent(lastIconExit ?? throw new Exception("The icon did not receive a pointer exit."));
Pump(320);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single),
    "A stale icon-exit timer cannot dismiss a preview while the pointer is still inside it.");
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
host.MouseMove(activatePoint);
Pump(30);
host.MouseDown(activatePoint, MouseButton.Left);
host.MouseUp(activatePoint, MouseButton.Left);
Check(first.IsActive && previewState.OpenTaskbarGroup is null, "Clicking a taskbar thumbnail activates the window and dismisses the strip.");
host.MouseMove(leftPoint);
Pump(30);
Check(left.IsPointerOver, "The next icon receives a fresh hover after the thumbnail click.");
right.RaiseEvent(lastRightExit ?? throw new Exception("The second icon did not receive a pointer exit."));
left.RaiseEvent(lastIconExit ?? throw new Exception("The first icon did not receive a pointer exit."));
Pump(450);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single),
    "After a thumbnail click, a late leave event from the previous application cannot cancel the next hover.");
host.MouseMove(panel.TranslatePoint(new Point(80, 70), host)!.Value);
Pump(320);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single), "The next preview remains usable after reopening by hover.");
// Replay the production trace: after a click, the parent hover flag becomes false
// although native input coordinates remain inside the thumbnail. Headless normally
// keeps that flag correct, so inject only the contradictory flag/leave notification.
typeof(InputElement).GetProperty(nameof(InputElement.IsPointerOver))!.SetValue(panel, false);
Check(!panel.IsPointerOver, "Regression fixture reproduces the false parent hover flag from the Windows trace.");
panel.RaiseEvent(lastPanelExit ?? throw new Exception("The panel did not receive a pointer exit."));
Pump(320);
Check(ReferenceEquals(previewState.OpenTaskbarGroup, single),
    "A false panel hover flag after a thumbnail click cannot close a preview while input coordinates are inside.");
host.MouseMove(new Point(10, 10));
typeof(InputElement).GetProperty(nameof(InputElement.IsPointerOver))!.SetValue(panel, true);
Pump(320);
Check(previewState.OpenTaskbarGroup is null,
    "Leaving the preview closes it even when its parent hover flag is stale and true.");
taskbarPreviews.Dismiss();
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
RunDesktopShellInteractionRegression(host, settings, localization, services);
host.Close();
using (var startup = JsonDocument.Parse(File.ReadLines(TaskbarPreviewDiagnostics.FilePath).First()))
{
    Check(startup.RootElement.GetProperty("event").GetString() == "session.start"
        && startup.RootElement.GetProperty("data").GetProperty("buildId").GetGuid() != Guid.Empty,
        "Trace identifies the running executable and client build.");
}
var traceLines = File.ReadAllLines(TaskbarPreviewDiagnostics.FilePath);
var traceEvents = new List<string>();
long previousSequence = 0;
foreach (var line in traceLines)
{
    using var entry = JsonDocument.Parse(line);
    var sequence = entry.RootElement.GetProperty("seq").GetInt64();
    CheckSequence(sequence == ++previousSequence);
    traceEvents.Add(entry.RootElement.GetProperty("event").GetString()!);
}
Check(traceEvents.Count(name => name == "vm.activate.begin") == 6
    && traceEvents.Count(name => name == "vm.activate.end") == 6,
    "Trace records all six real-shell thumbnail activations from start to completion.");
Check(new[] { "view.created", "icon.enter", "host.pointerMove", "card.press", "card.release", "card.click", "card.captureLost",
        "open.timerOrSwitch", "close.timer", "dismiss.begin", "group.changed.hidden", "manager.activeChanged" }
    .All(traceEvents.Contains), "Trace covers pointer capture, timers, dismissal and activation in event order.");
Check(!traceLines.Any(line => line.Contains("Preview document") || line.Contains("First real document")),
    "Trace excludes window titles and document content.");
Console.WriteLine($"Interaction trace: {TaskbarPreviewDiagnostics.FilePath}");
LanguageSwitchChecks.Run(settings, localization);
MemoryLifecycleChecks.Run(settings, localization, services);
Console.WriteLine($"PASS: window preview rendering, caching, native fallback, activation and close lifecycle. QA image: {output}");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.Exit(1);
}

static void CheckSequence(bool valid)
{
    if (!valid) throw new Exception("Trace sequence is incomplete or unordered.");
}

static void Pump(int milliseconds)
{
    using var cancellation = new CancellationTokenSource(milliseconds);
    Dispatcher.UIThread.MainLoop(cancellation.Token);
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}

static void RunDesktopShellInteractionRegression(Window host, ShellSettings settings,
    LocalizationService localization, IServiceProvider services)
{
    var windows = new WindowManagerService();
    var session = System.Reflection.DispatchProxy.Create<IAuthSession, UnusedPreviewServices>();
    // This fixture exercises only the actual window/taskbar commands; no remote file,
    // workspace persistence, application launch or desktop restoration operation is invoked.
    var vm = new DesktopShellViewModel(windows, new ApplicationManager(windows, services), settings,
        localization, session, new SshDesktopSession(null!), () => { },
        null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
    var store = new ShellStateStore();
    store.Publish(vm);
    var surfaces = new PreviewSurfaceRegistry(windows);
    var shell = new WindowsLikeDesktopShell();
    shell.InitializeAsync(new ShellPresentationContext(store, null!, null!, surfaces, null!), CancellationToken.None)
        .GetAwaiter().GetResult();
    host.Content = shell.View;
    windows.Attach(surfaces.Surfaces!.WindowHost);
    windows.AttachFullScreenHost(surfaces.Surfaces.FullScreenWindowHost);
    shell.ActivateAsync(CancellationToken.None).GetAwaiter().GetResult();
    Pump(50);
    var app = new AppId("real.preview.tests");
    var first = windows.Create(new WindowCreateOptions(app, "First real document",
        new Border { Background = Brushes.Crimson }, new WindowRect(30, 30, 500, 350), IconGlyph: "□"));
    var second = windows.Create(new WindowCreateOptions(app, "Second real document",
        new Border { Background = Brushes.RoyalBlue }, new WindowRect(80, 80, 500, 350), IconGlyph: "□"));
    Pump(50);
    var group = vm.TaskbarGroups.Single();
    vm.ShowTaskbarPreview(group);
    Pump(50);
    var beforeFocus = shell.View.GetVisualDescendants().OfType<Button>().Single(button =>
        ReferenceEquals(button.Command, vm.ActivateTaskbarWindowCommand) && ReferenceEquals(button.CommandParameter, first));
    windows.Focus(first);
    Pump(50);
    var afterFocus = shell.View.GetVisualDescendants().OfType<Button>().Single(button =>
        ReferenceEquals(button.Command, vm.ActivateTaskbarWindowCommand) && ReferenceEquals(button.CommandParameter, first));
    Check(ReferenceEquals(beforeFocus, afterFocus), "Real shell: activating a window preserves the existing preview controls and pointer state.");
    vm.CloseTaskbarPreviewCommand.Execute(null);
    var other = windows.Create(new WindowCreateOptions(new AppId("real.preview.other"), "Another application",
        new Border { Background = Brushes.Gold }, new WindowRect(120, 120, 500, 350), IconGlyph: "□"));
    var otherGroup = vm.TaskbarGroups.Single(item => item.AppId == other.Info.OwnerAppId);
    for (var round = 0; round < 6; round++)
    {
        if (round == 4) windows.Close(second);
        var currentGroup = round == 2 ? otherGroup : group;
        host.MouseMove(new Point(1100, 100));
        Pump(320);
        var icon = shell.View.GetVisualDescendants().OfType<Button>().Single(button =>
            ReferenceEquals(button.Command, vm.ToggleTaskbarGroupCommand) && ReferenceEquals(button.CommandParameter, currentGroup));
        var point = icon.TranslatePoint(new Point(20, 20), host)!.Value;
        host.MouseMove(point);
        if (round == 0)
        {
            Pump(30);
            host.MouseDown(point, MouseButton.Left);
            host.MouseUp(point, MouseButton.Left);
            Pump(50);
        }
        else Pump(450);
        Check(ReferenceEquals(vm.OpenTaskbarGroup, currentGroup), $"Real shell round {round + 1}: preview opens again after the previous thumbnail click.");
        var target = round == 2 ? other : round >= 4 || round % 2 == 0 ? first : second;
        var card = shell.View.GetVisualDescendants().OfType<Button>().Single(button =>
            ReferenceEquals(button.Command, vm.ActivateTaskbarWindowCommand) && ReferenceEquals(button.CommandParameter, target));
        var cardPoint = card.TranslatePoint(new Point(80, 60), host)!.Value;
        host.MouseMove(cardPoint);
        Pump(500);
        Check(ReferenceEquals(vm.OpenTaskbarGroup, currentGroup), $"Real shell round {round + 1}: hovering the thumbnail keeps the preview usable.");
        var activeChanges = !target.IsActive;
        var previewCollapsedBeforeActivation = false;
        EventHandler<ManagedWindow?> observedActivation = (_, window) =>
        {
            if (ReferenceEquals(window, target)) previewCollapsedBeforeActivation = vm.OpenTaskbarGroup is null;
        };
        windows.ActiveWindowChanged += observedActivation;
        host.MouseDown(cardPoint, MouseButton.Left);
        host.MouseUp(cardPoint, MouseButton.Left);
        windows.ActiveWindowChanged -= observedActivation;
        Pump(50);
        Check(target.IsActive && vm.OpenTaskbarGroup is null, $"Real shell round {round + 1}: clicking the thumbnail activates the window.");
        if (activeChanges) Check(previewCollapsedBeforeActivation, "Real shell: the preview releases focus before application activation.");
    }
    windows.Close(first);
    windows.Close(second);
    windows.Close(other);
    shell.DisposeAsync().GetAwaiter().GetResult();
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

public class UnusedPreviewServices : System.Reflection.DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    {
        if (method!.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        if (method.Name.StartsWith("get_", StringComparison.Ordinal))
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        throw new InvalidOperationException($"Unexpected remote service invocation in a taskbar test: {method.Name}");
    }
}

internal sealed class PreviewSurfaceRegistry(WindowManagerService windows) : IShellSurfaceRegistry
{
    public ShellSurfaces? Surfaces { get; private set; }
    public void Register(ShellSurfaces surfaces) => Surfaces = surfaces;
    public void Clear() => Surfaces = null;
    public void UpdateWorkArea(WindowRect workArea) => windows.SetHostBounds(workArea);
}
