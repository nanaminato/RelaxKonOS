using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Apps.AppInstaller.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;

AppBuilder.Configure<LayoutApp>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
using var appearance = new AppearanceService(Application.Current!, new SystemStyleRegistry());
var settings = new ShellSettings(appearance);
var localization = new LocalizationService(settings, new SshDesktopSession(null!));
using var services = new ServiceCollection().AddSingleton(localization).BuildServiceProvider();
typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);
var vm = new DockerManagerViewModel(null!)
{
    StatusText = LocalizedText.Ref("docker.status.available", "27.0", "Linux"),
    ContainerLogs = "web-service: started successfully\nweb-service: listening on port 8080",
    ContainerStats = "CPU 0.4%   Memory 64 MB / 512 MB"
};
for (var i = 0; i < 40; i++)
{
    vm.Images.Add(new DockerImageDto($"image-{i}", $"example/long-repository-name-{i}", "latest", "128 MB", "2 days ago"));
    vm.Containers.Add(new DockerContainerDto($"container-{i}", $"web-service-{i}", "example/web:latest", "running", "Up 2 days"));
    vm.Networks.Add(new DockerNetworkDto($"network-{i}", $"application-network-{i}", "bridge", "local"));
    vm.Volumes.Add(new DockerVolumeDto($"volume-{i}", "local", $"/var/lib/docker/volumes/application-data-{i}"));
}
var output = Path.Combine(AppContext.BaseDirectory, "layout-qa");
Directory.CreateDirectory(output);
var host = new Window { Width = 1000, Height = 700 };
host.Show();
var cases = 0;
foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
foreach (var mode in new[] { ThemeKind.Light, ThemeKind.Dark })
foreach (var style in new[] { SystemStyleIds.WindowsLike, SystemStyleIds.MacOsLike, SystemStyleIds.UbuntuLike })
foreach (var page in new[] { "Containers", "Images", "Networks", "Volumes" })
{
    settings.Language = culture;
    appearance.Apply(mode, AppearancePreferencesDto.Default, style);
    var type = typeof(DockerManagerViewModel).Assembly.GetType("RelaxKonOS.Client.Apps.Docker.Views.DockerManagerWorkspace")!;
    Func<Task> noOp = () => Task.CompletedTask;
    var view = (Control)type.GetMethod("Create")!.Invoke(null,
        new object?[] { vm, null, null, noOp, noOp, noOp, noOp, noOp })!;
    host.Content = view;
    host.Width = 800;
    host.Height = 520;
    Dispatcher.UIThread.RunJobs();
    var navigation = view.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == page.ToLowerInvariant());
    navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    Check(navigation.Classes.Contains("nav-selected"), "The selected resource page retains its navigation state.");
    var table = view.GetVisualDescendants().OfType<DataGrid>().Single();
    var compactHeight = table.Bounds.Height;
    Check(compactHeight > 40, $"{page}: compact resource list remains usable ({compactHeight}).");
    Check(table.Bounds.Width <= view.Bounds.Width, $"{page}: the table fits its viewport.");
    if (page == "Containers")
    {
        vm.ContainerLogs = string.Empty;
        vm.ContainerStats = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Check(table.Bounds.Height > compactHeight + 60, "Empty logs and stats return their space to the resource list.");
        vm.ContainerLogs = "web-service: started successfully\nweb-service: listening on port 8080";
        vm.ContainerStats = "CPU 0.4%   Memory 64 MB / 512 MB";
        Dispatcher.UIThread.RunJobs();
    }
    Check(view.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && b.Classes.Contains("primary"))
        .All(b => b.TranslatePoint(default, view) is { } p && p.Y >= 0 && p.Y + b.Bounds.Height <= view.Bounds.Height + 1),
        $"{page}: create action remains inside the page.");
    Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("[missing:") == true),
        $"{page}: all rendered labels resolve in {culture}.");
    host.Height = 800;
    Dispatcher.UIThread.RunJobs();
    Check(table.Bounds.Height > compactHeight + 200, $"{page}: growing the window grows the list.");
    // Capture each page/theme/style once; language combinations still run the layout checks.
    if (culture == "zh-CN")
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(800, 800));
        bitmap.Render(view);
        bitmap.Save(Path.Combine(output, $"{page}-{mode}-{style}.png"), PngBitmapEncoderOptions.Default);
    }
    cases++;
}
foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
foreach (var mode in new[] { ThemeKind.Light, ThemeKind.Dark })
foreach (var style in SystemStyleIds.All)
{
    settings.Language = culture;
    appearance.Apply(mode, AppearancePreferencesDto.Default, style);
    using var installer = new AppInstallerViewModel(null!);
    installer.CurrentPackage = new AppPackageCandidate("sample.roapp", "Local sample",
        new DeveloperPackageManifest("layout.sample", "Sample application", "1.0.0", "Sample.dll", "Sample.App",
            Description: "A package with a long permission list.", RequestedPermissions: AppPermissions.All.Select(p => p.Id).ToArray()),
        null, false);
    var view = new AppInstallerView { DataContext = installer };
    host.Width = 800;
    host.Height = 520;
    host.Content = view;
    Dispatcher.UIThread.RunJobs();
    var scroll = ((Grid)view.Content!).Children.OfType<ScrollViewer>().Single();
    var install = view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("primary"));
    var before = install.TranslatePoint(default, view)!.Value;
    Check(scroll.Extent.Height > scroll.Viewport.Height + 100, "Long package permissions require scrolling.");
    Check(before.Y + install.Bounds.Height <= view.Bounds.Height, "Install remains inside the compact viewport.");
    scroll.Offset = new Vector(0, 10000);
    Dispatcher.UIThread.RunJobs();
    Check(install.TranslatePoint(default, view)!.Value == before, "Scrolling permissions leaves the decision footer fixed.");
    if (culture == "zh-CN")
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(800, 520));
        bitmap.Render(view);
        bitmap.Save(Path.Combine(output, $"Installer-{mode}-{style}.png"), PngBitmapEncoderOptions.Default);
    }
    installer.CurrentPackage = null; // The sample has no installer service or package file to discard.
    cases++;
}
host.Close();
Console.WriteLine($"PASS: {cases} real-page layouts across 3 languages, 2 modes and 3 system styles. Screenshots: {output}");
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
public sealed class LayoutApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/"))
            { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://RelaxKonOS.UI/"))
            { Source = new Uri("avares://RelaxKonOS.UI/Themes/RelaxKonOSTheme.axaml") });
    }
}
