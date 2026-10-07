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
if (args.Contains("--browser-only"))
{
    BrowserChecks.Run(host, settings, appearance, output);
    host.Close();
    return;
}
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
// Operation activity keeps a compact status row; output is opened only on request.
foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
{
    settings.Language = culture;
    vm.OperationTitle = "Restart test3";
    vm.OperationStatus = LocalizedText.Ref("docker.operation.running_label");
    vm.OperationLog = string.Join(Environment.NewLine, Enumerable.Range(1, 80).Select(i => $"[{i}] service output"));
    vm.IsOperationRunning = true;
    vm.IsOperationLogExpanded = false;
    var type = typeof(DockerManagerViewModel).Assembly.GetType("RelaxKonOS.Client.Apps.Docker.DockerOperationActivityView")!;
    var activity = (Control)Activator.CreateInstance(type)!;
    activity.DataContext = vm;
    host.Width = 800;
    host.Height = 520;
    host.Content = activity;
    Dispatcher.UIThread.RunJobs();
        Check(activity.Bounds.Height <= 40, "Collapsed activity uses only one status row.");
    var toggle = activity.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Name == "OperationLogToggle");
    Check(toggle.IsChecked != true, "Logs stay hidden until requested.");
    toggle.IsChecked = true;
    Dispatcher.UIThread.RunJobs();
    Check(vm.IsOperationLogExpanded, "The log entry opens the output panel.");
    Check(activity.Bounds.Height <= 232, "Expanded output remains bounded.");
    Check(activity.GetVisualDescendants().OfType<ProgressBar>().Single().IsVisible, "Running activity has a progress indicator.");
    var log = activity.GetVisualDescendants().OfType<TextBox>().Single();
    Check(log.Text == vm.OperationLog && log.Bounds.Height == 180 && log.IsEffectivelyVisible, "Logs remain readable in a bounded viewport.");
    toggle.IsChecked = false;
    Dispatcher.UIThread.RunJobs();
    Check(activity.Bounds.Height <= 40 && vm.IsOperationRunning, "Hiding output restores space without stopping the operation.");
    vm.CloseOperationActivityCommand.Execute(null);
    Check(vm.HasOperationActivity, "Running activity cannot be dismissed accidentally.");
    vm.IsOperationRunning = false;
    Dispatcher.UIThread.RunJobs();
    Check(!activity.GetVisualDescendants().OfType<ProgressBar>().Single().IsVisible, "Completed activity stops animating.");
    vm.CloseOperationActivityCommand.Execute(null);
    Check(!vm.HasOperationActivity, "Completed activity can be dismissed.");
    if (culture == "zh-CN")
    {
        appearance.Apply(ThemeKind.Light, AppearancePreferencesDto.Default, SystemStyleIds.WindowsLike);
        var workspaceType = typeof(DockerManagerViewModel).Assembly.GetType("RelaxKonOS.Client.Apps.Docker.Views.DockerManagerWorkspace")!;
        Func<Task> noOp = () => Task.CompletedTask;
        var workspace = (Control)workspaceType.GetMethod("Create")!.Invoke(null, new object?[] { vm, null, null, noOp, noOp, noOp, noOp, noOp })!;
        vm.OperationTitle = "Restart test3";
        vm.IsOperationRunning = true;
        vm.OperationStatus = LocalizedText.Ref("docker.operation.running_label");
        vm.OperationLog = string.Join(Environment.NewLine, Enumerable.Range(1, 80).Select(i => $"[{i}] service output"));
        host.Content = workspace;
        foreach (var expanded in new[] { false, true })
        {
            vm.IsOperationLogExpanded = expanded;
            Dispatcher.UIThread.RunJobs();
            var bar = workspace.GetVisualDescendants().Single(c => c.GetType() == type);
            var point = bar.TranslatePoint(default, workspace)!.Value;
            Check(Math.Abs(point.Y + bar.Bounds.Height - workspace.Bounds.Height) <= 1, "Activity is docked at the bottom of the workspace.");
            using var bitmap = new RenderTargetBitmap(new PixelSize(800, 520));
            bitmap.Render(workspace);
            bitmap.Save(Path.Combine(output, expanded ? "Activity-expanded.png" : "Activity-compact.png"), PngBitmapEncoderOptions.Default);
        }
        vm.IsOperationRunning = false;
        vm.CloseOperationActivityCommand.Execute(null);
    }
    cases++;
}
foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
foreach (var mode in new[] { ThemeKind.Light, ThemeKind.Dark })
{
    settings.Language = culture;
    appearance.Apply(mode, AppearancePreferencesDto.Default, SystemStyleIds.WindowsLike);
    var certificateVm = new RelaxKonOS.Client.Apps.Certificates.CertificateManagerViewModel(null!, null!, new LayoutCertificatePermissions());
    var now = DateTimeOffset.UtcNow;
    var certificate = new RelaxKonOS.Protocol.Certificates.CertificateDto(Guid.NewGuid(), "192.168.1.2", ["192.168.1.2", "server.example.test"], "CN=192.168.1.2", "123", "123", now, now.AddDays(365),
        RelaxKonOS.Protocol.Certificates.CertificateStatus.Active, RelaxKonOS.Protocol.Certificates.CertificateChallengeType.Dns01,
        RelaxKonOS.Protocol.Certificates.CertificateKeyAlgorithm.EcdsaP256, null, null, null, null, now, now,
        FingerprintSha256: string.Join(':', Enumerable.Repeat("AB", 32)));
    certificateVm.Certificates.Add(certificate);
    certificateVm.SelectedCertificate = certificate;
    Check(certificateVm.RenewCommand.CanExecute(null) && certificateVm.RevokeCommand.CanExecute(null), "ACME lifecycle actions are available.");
    certificateVm.SelectedCertificate = certificate with { Kind = RelaxKonOS.Protocol.Certificates.CertificateKind.SelfSigned };
    Check(!certificateVm.RenewCommand.CanExecute(null) && !certificateVm.RevokeCommand.CanExecute(null) && certificateVm.DeleteCommand.CanExecute(null), "Self-signed certificates can be deleted but cannot use ACME renewal or revocation.");
    certificateVm.SelectedCertificate = certificate with { Status = RelaxKonOS.Protocol.Certificates.CertificateStatus.Revoked };
    Check(!certificateVm.RenewCommand.CanExecute(null) && !certificateVm.RevokeCommand.CanExecute(null), "Revoked certificates cannot repeat ACME lifecycle actions.");
    certificateVm.SelectedCertificate = certificate;
    var httpsView = new RelaxKonOS.Client.Apps.Settings.Views.ServerHttpsDialogView { DataContext = certificateVm };
    host.Width = 660; host.Height = 480; host.Content = httpsView;
    Dispatcher.UIThread.RunJobs();
    Check(ReferenceEquals(httpsView.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem, certificate), "HTTPS selection uses the managed certificate.");
    var replace = httpsView.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("primary"));
    Check(replace.TranslatePoint(default, httpsView) is { } point && point.Y >= 0 && point.Y + replace.Bounds.Height <= httpsView.Bounds.Height, "HTTPS replacement action fits the dialog.");
    Check(certificateVm.SelectedCertificateDomains.Contains("192.168.1.2") && certificateVm.SelectedCertificateDomains.Contains("server.example.test"), "HTTPS replacement presents all certificate names.");
    Check(!httpsView.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("[missing:") == true), "HTTPS labels resolve in every language.");
    Check(!certificateVm.HasOperationActivity, "Opening HTTPS settings does not change the server certificate.");
    var failure = new RelaxKonOS.Protocol.Certificates.CertificateOperationDto(Guid.NewGuid(), certificate.Id, "renew",
        RelaxKonOS.Protocol.Certificates.CertificateOperationState.Failed, "failed", "certificate.validation_failed", now.AddMinutes(-2), now);
    var renewed = certificate with { Renewal = new(true, 1, now.AddDays(1), false, [new(true, failure)]) };
    certificateVm.Certificates[0] = renewed;
    certificateVm.SelectedCertificate = renewed;
    Check(certificateVm.SelectedRenewalText.Contains(LocalizedText.Get("certificates.renewal.automatic"))
        && certificateVm.SelectedRenewalText.Contains(LocalizedText.Get("certificates.renewal.state.failed"))
        && !certificateVm.SelectedRenewalText.Contains("{0}") && !certificateVm.SelectedRenewalText.Contains("[missing:"),
        "Renewal failure has localized source, status and formatted times.");
    var historyRequested = false;
    var renewalView = (Control)Activator.CreateInstance(typeof(RelaxKonOS.Client.Apps.Certificates.CertificateManagerViewModel).Assembly
        .GetType("RelaxKonOS.Client.Apps.Certificates.Views.CertificateListView")!, new object[] { (Func<Task>)(() => Task.CompletedTask), (Func<Task>)(() => Task.CompletedTask), (Action)(() => historyRequested = true) })!;
    renewalView.DataContext = certificateVm;
    host.Content = renewalView; host.Width = 800; host.Height = 650;
    Dispatcher.UIThread.RunJobs();
    Check(!renewalView.GetVisualDescendants().OfType<Expander>().Any(), "Renewal history is a separate view and preserves certificate table space.");
    var historyButton = renewalView.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == LocalizedText.Get("certificates.renewal.title"));
    historyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(historyRequested, "Renewal history action invokes the current navigation callback.");
    Check(historyButton.TranslatePoint(default, renewalView) is { } renewalPoint && renewalPoint.Y + historyButton.Bounds.Height <= renewalView.Bounds.Height,
        "Renewal history action fits the certificate workspace.");
    if (culture == "zh-CN") {
        using var renewalBitmap = new RenderTargetBitmap(new PixelSize(800, 650));
        renewalBitmap.Render(renewalView);
        renewalBitmap.Save(Path.Combine(output, $"CertificateRenewal-{mode}.png"), PngBitmapEncoderOptions.Default);
    }
    host.Content = httpsView;
    if (culture == "zh-CN")
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(660, 480));
        bitmap.Render(httpsView);
        bitmap.Save(Path.Combine(output, $"ServerHttps-{mode}.png"), PngBitmapEncoderOptions.Default);
    }
    cases++;
}

// Remaining built-in operations share a one-row footer and opt-in output.
foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
{
    settings.Language = culture;
    using var tunnels = new RelaxKonOS.Client.Apps.Tunnels.TunnelManagerViewModel(null!, true);
    tunnels.IsBusy = true;
    tunnels.StatusText = "Applying tunnel";
    var tunnelView = new RelaxKonOS.Client.Apps.Tunnels.Views.TunnelManagerView { DataContext = tunnels };
    host.Width = 800; host.Height = 520; host.Content = tunnelView;
    Dispatcher.UIThread.RunJobs();
    var log = tunnelView.GetVisualDescendants().OfType<TextBox>().Single();
    var toggle = tunnelView.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single();
    Check(!log.IsEffectivelyVisible, "Tunnel output is hidden by default.");
    var footer = log.GetVisualAncestors().OfType<Border>().First();
    Check(footer.Bounds.Height <= 40, "Tunnel status uses one row.");
    toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
    Check(log.IsEffectivelyVisible && log.Text!.Contains("Applying tunnel"), "Tunnel output opens and includes current work.");
    toggle.IsChecked = false; Dispatcher.UIThread.RunJobs();
    Check(tunnels.IsBusy && footer.Bounds.Height <= 40, "Closing tunnel output returns space without stopping work.");
    if (culture == "zh-CN")
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(800, 520));
        bitmap.Render(tunnelView);
        bitmap.Save(Path.Combine(output, "Tunnels-compact.png"), PngBitmapEncoderOptions.Default);
    }
    cases++;
    var installation = new RelaxKonOS.Client.Services.Installation.InstallationTaskViewModel(null!, null!, RelaxKonOS.Protocol.Installations.InstallationServiceId.Frp, "layout.test", () => Task.CompletedTask, () => Task.FromResult<string?>(null));
    var wrapper = RelaxKonOS.Client.Services.Installation.InstallationPanel.Wrap(new Border(), installation);
    host.Content = wrapper; Dispatcher.UIThread.RunJobs();
    installation.ConnectionText = "";
    installation.Operation = new RelaxKonOS.Protocol.Installations.InstallationOperationDto(Guid.NewGuid(), RelaxKonOS.Protocol.Installations.InstallationServiceId.Frp, RelaxKonOS.Protocol.Installations.InstallationOperationKind.Install, RelaxKonOS.Protocol.Installations.InstallationOperationState.Running, RelaxKonOS.Protocol.Installations.InstallationStage.Installing, 35, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, true);
    Dispatcher.UIThread.RunJobs();
    var installationLog = wrapper.GetVisualDescendants().OfType<TextBox>().Single();
    var installationPanel = (Control)((DockPanel)wrapper).Children[0];
    Check(installationPanel.Bounds.Height <= 38 && !installationLog.IsEffectivelyVisible, "Shared installation feedback is compact by default.");
    var installationToggle = wrapper.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single();
    installationToggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
    Check(installationLog.IsEffectivelyVisible && installationLog.Text!.Contains("35%"), "Installation output includes observed stage progress.");
    installationToggle.IsChecked = false; Dispatcher.UIThread.RunJobs();
    Check(installation.IsActive && installationPanel.Bounds.Height <= 38, "Hiding installation output does not cancel installation.");
    cases++;
}
BrowserChecks.Run(host, settings, appearance, output);
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
public sealed class LayoutCertificatePermissions : RelaxKonOS.AppSDK.IAppPermissionScope
{
    public RelaxKonOS.AppSDK.AppPermissionStatus GetStatus(string permissionId) => RelaxKonOS.AppSDK.AppPermissionStatus.Granted;
    public bool IsGranted(string permissionId) => true;
    public Task<RelaxKonOS.AppSDK.AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default) => Task.FromResult(GetStatus(permissionId));
    public Task OpenSettingsAsync() => Task.CompletedTask;
}
