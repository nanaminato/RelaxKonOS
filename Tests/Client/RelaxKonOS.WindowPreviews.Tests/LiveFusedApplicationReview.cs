using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.Runtime;
using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

/// <summary>Opt-in review of the actual desktop client. Credentials are supplied only through
/// process environment variables; the normal auth API is used without remembering credentials.</summary>
internal static class LiveFusedApplicationReview
{
    public static void Run(string[] args)
    {
        AppBuilder.Configure<RelaxKonOS.Client.App>().UsePlatformDetect()
            .AfterSetup(_ => Dispatcher.UIThread.Post(async () => await OpenAsync(args)))
            .StartWithClassicDesktopLifetime(args);
    }

    private static async Task OpenAsync(string[] args)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (RelaxKonOS.Client.App.Services is null
                || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: not null })
                await Task.Delay(100, timeout.Token);
            var services = RelaxKonOS.Client.App.Services;
            var endpoint = Environment.GetEnvironmentVariable("RK_REVIEW_SERVER") ?? throw new InvalidOperationException("Missing review endpoint.");
            var identifier = Environment.GetEnvironmentVariable("RK_REVIEW_USER") ?? throw new InvalidOperationException("Missing review user.");
            var password = Environment.GetEnvironmentVariable("RK_REVIEW_PASSWORD") ?? throw new InvalidOperationException("Missing review password.");
            Environment.SetEnvironmentVariable("RK_REVIEW_PASSWORD", null);
            if (args.Contains("--fused-ssh-review"))
            {
                await LiveSshFusedReview.RunAsync(services, new Uri(endpoint).Host, identifier, password, timeout.Token);
                password = string.Empty;
                (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown();
                return;
            }
            var auth = services.GetRequiredService<IAuthSession>();
            var identity = new ServerConnectionIdentity(ServerServiceIdKind.DirectUrl, endpoint, endpoint);
            var request = new LoginRequest(identifier, password, ClientPlatformKind.Windows, "Fused header UI review", "1.0.0");
            try { await auth.LoginAsync(identity, request, false, false, timeout.Token); }
            catch (HttpRequestException) when (Environment.GetEnvironmentVariable("RK_REVIEW_TRUST_CURRENT_CERT") == "1")
            {
                var trust = ServerCertificateTrust.Shared;
                var review = trust.GetReview(endpoint);
                if (review is null) throw;
                if (!review.CanTrust)
                    throw new InvalidOperationException($"Certificate cannot be pinned by the normal validator: {review.Errors}.");
                // Test-only opt-in after human approval. Reuse the production fingerprint/date/name
                // validator but insert the pin in memory, without calling Trust (which writes disk).
                var pins = (ConcurrentDictionary<string, string>)typeof(ServerCertificateTrust)
                    .GetField("_pins", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(trust)!;
                pins[review.TrustScope ?? review.Origin] = review.Fingerprint;
                await auth.LoginAsync(identity, request, false, false, timeout.Token);
            }
            password = string.Empty;
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            while (desktop.MainWindow is not RelaxKonOS.Client.Views.MainWindow)
                await Task.Delay(100, timeout.Token);
            var windows = services.GetRequiredService<RelaxKonOS.WindowManager.IWindowManager>();
            while (windows.HostBounds.Width <= 0
                || desktop.MainWindow.FindControl<Control>("DesktopLoadingOverlay")!.IsVisible
                || windows.IsSystemModalOpen) await Task.Delay(100, timeout.Token);
            var preferences = services.GetRequiredService<PreferencesSync>();
            await preferences.EnsureCurrentWorkspacePreferencesAsync();
            // This preview changes appearance only in memory. Window-layout writes also publish
            // preference revisions; freeze the review snapshot so they cannot replace its style.
            (typeof(PreferencesSync).GetField("_streamCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(preferences) as CancellationTokenSource)?.Cancel();
            var settings = services.GetRequiredService<ShellSettings>();
            var originalStyle = settings.SystemStyleId;
            var originalAppearance = settings.Appearance;
            var previewStyle = SystemStyleIds.WindowsLike;
            var previewDark = false;
            void ApplyPreview()
            {
                var mode = previewDark ? RelaxKonOS.Protocol.Desktop.ThemeKind.Dark : RelaxKonOS.Protocol.Desktop.ThemeKind.Light;
                settings.SystemStyleId = previewStyle;
                settings.Appearance = settings.Appearance with { Mode = mode };
            }
            desktop.MainWindow.Closed += (_, _) =>
            {
                settings.SystemStyleId = originalStyle;
                settings.Appearance = originalAppearance;
            };
            ApplyPreview();
            var apps = services.GetRequiredService<ApplicationManager>();
            apps.Launch(new AppId("relaxkonos.codeeditor"));
            apps.Launch(new AppId("relaxkonos.explorer"));
            if (args.Contains("--fused-all-apps-review"))
            {
                await ReviewAllAsync(services, desktop, windows, settings, apps, timeout.Token);
                desktop.Shutdown();
                return;
            }
            // Review shortcuts exist only in this opt-in test harness, never in production.
            desktop.MainWindow.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.F6)
                {
                    previewStyle = previewStyle == SystemStyleIds.WindowsLike
                        ? SystemStyleIds.MacOsLike : SystemStyleIds.WindowsLike;
                    ApplyPreview();
                    e.Handled = true;
                }
                if (e.Key == Key.F7)
                {
                    previewDark = !previewDark;
                    ApplyPreview();
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            Console.WriteLine("LIVE REVIEW READY: real editor/explorer; F6 changes chrome, F7 changes theme.");
        }
        catch (Exception ex)
        {
            if (ex is RelaxKonOS.Client.Services.ServerCenter.ServerCenterHostKeyRejectedException ssh)
                Console.Error.WriteLine($"SSH REVIEW: host={ssh.Observation.Host}:{ssh.Observation.Port}; algorithm={ssh.Observation.Algorithm}; fingerprint={ssh.Observation.Fingerprint}; trust={ssh.Trust}");
            if (Environment.GetEnvironmentVariable("RK_REVIEW_SERVER") is { } server
                && ServerCertificateTrust.Shared.GetReview(server) is { } review)
                Console.Error.WriteLine($"TLS REVIEW: subject={review.Subject}; SHA256={review.Fingerprint}; errors={review.Errors}; canPin={review.CanTrust}; valid={review.NotBefore:O}..{review.NotAfter:O}");
            Console.Error.WriteLine($"LIVE REVIEW FAILED: {ex}");
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(1);
        }
    }

    private static async Task ReviewAllAsync(IServiceProvider services, IClassicDesktopStyleApplicationLifetime desktop,
        RelaxKonOS.WindowManager.IWindowManager windows, ShellSettings settings, ApplicationManager apps, CancellationToken ct)
    {
        var ids = new[] { "server-center", "git", "docker", "proxy", "tunnels", "port-forwarding", "webservers", "certificates",
            "application-deployments", "file-services", "notepad", "taskmanager", "imageviewer", "browser" };
        foreach (var id in ids)
        {
            var launched = apps.Launch(new AppId("relaxkonos." + id));
            Console.WriteLine($"LIVE LAUNCH: {id} accepted={launched}");
            await Task.Delay(150, ct);
        }
        await Task.Delay(2000, ct);
        if (windows.IsSystemModalOpen) throw new InvalidOperationException("Live review needs a system permission decision; no automatic approval was applied.");
        var gitWindow = windows.Windows.FirstOrDefault(window => window.Info.OwnerAppId.Value == "relaxkonos.git");
        if (gitWindow?.View.Content is Control gitRoot)
        {
            var gitModel = gitRoot.DataContext as RelaxKonOS.Client.Apps.Git.GitClientViewModel
                ?? gitRoot.GetVisualDescendants().OfType<Control>().Select(control => control.DataContext).OfType<RelaxKonOS.Client.Apps.Git.GitClientViewModel>().FirstOrDefault();
            if (gitModel is { IsPickerMode: true } && gitModel.Repositories.FirstOrDefault() is { } repository)
            {
                // Only open an existing registered repository; never create or modify one for QA.
                var open = gitModel.OpenProjectCommand.ExecuteAsync(repository);
                await Task.Delay(500, ct);
                Console.WriteLine($"LIVE GIT WORKSPACE: existing repository opened; taskCompleted={open.IsCompleted}; fused={gitWindow.View.HasTitleBarContent}.");
            }
            else Console.WriteLine("LIVE GIT PICKER: no existing repository selected; picker boundary is reviewed without creating test data.");
        }
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "live-fused-workspaces");
        Directory.CreateDirectory(output);
        var snapshots = windows.Windows.ToArray();
        foreach (var id in ids)
            if (!snapshots.Any(window => window.Info.OwnerAppId.Value == "relaxkonos." + id))
                Console.WriteLine($"LIVE UNAVAILABLE: {id} did not create a window for this session type/capability.");
        var checkedWindows = 0;
        foreach (var window in snapshots)
        {
            var name = window.Info.OwnerAppId.Value;
            if (!ids.Select(id => "relaxkonos." + id).Contains(name)
                && name is not ("relaxkonos.codeeditor" or "relaxkonos.explorer")) continue;
            if (!window.View.HasTitleBarContent)
            {
                Console.WriteLine($"LIVE TASK VIEW: {name} has no application header (picker or unavailable service).");
                continue;
            }
            windows.Focus(window);
            foreach (var other in snapshots) other.View.IsVisible = ReferenceEquals(other, window);
            foreach (var style in new[] { SystemStyleIds.WindowsLike, SystemStyleIds.MacOsLike, SystemStyleIds.UbuntuLike })
            foreach (var width in new[] { 960, 320 })
            {
                settings.SystemStyleId = style;
                window.View.Width = width; window.Info.Bounds = window.Info.Bounds with { Width = width };
                await Task.Delay(120, ct); desktop.MainWindow!.UpdateLayout();
                var controls = window.View.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "PART_WindowControls");
                var leading = window.View.HeaderCenter ?? window.View.HeaderTabs;
                if (leading is { IsEffectivelyVisible: true })
                {
                    var a = new Avalonia.Rect(leading.TranslatePoint(default, window.View)!.Value, leading.Bounds.Size);
                    var b = new Avalonia.Rect(controls.TranslatePoint(default, window.View)!.Value, controls.Bounds.Size);
                    if (a.Intersect(b).Width > 1) throw new InvalidOperationException($"Live header overlaps caption controls: {name}/{style}/{width}.");
                }
                using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.View.Bounds.Width), (int)Math.Ceiling(window.View.Bounds.Height)));
                image.Render(window.View);
                image.Save(Path.Combine(output, $"{name}-{style}-{width}.png"), new PngBitmapEncoderOptions());
            }
            if (window.View.HeaderTrailing is { } actions)
            {
                var buttons = actions.GetVisualDescendants().OfType<Button>().Concat(actions is Button button ? [button] : Array.Empty<Button>());
                var refresh = buttons.FirstOrDefault(button => Avalonia.Automation.AutomationProperties.GetName(button) == LocalizedRefresh());
                if (refresh?.Command is IAsyncRelayCommand command && command.CanExecute(refresh.CommandParameter))
                {
                    await command.ExecuteAsync(refresh.CommandParameter).WaitAsync(ct);
                    Console.WriteLine($"LIVE REFRESH: {name} completed against the authenticated session.");
                }
            }
            checkedWindows++;
            Console.WriteLine($"LIVE HEADER PASS: {name}; three recipes and normal/narrow widths.");
        }
        foreach (var window in snapshots) windows.ForceClose(window);
        if (checkedWindows == 0) throw new InvalidOperationException("No authenticated fused application windows were reviewed.");
        Console.WriteLine($"LIVE REVIEW PASS: {checkedWindows} fused windows; screenshots in {output}. Backend feature availability is recorded per launch.");

        static string LocalizedRefresh() => RelaxKonOS.Client.Localization.LocalizedText.Get("common.refresh");
    }
}
