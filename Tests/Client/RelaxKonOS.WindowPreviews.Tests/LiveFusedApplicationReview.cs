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

/// <summary>Opt-in review of the actual desktop client. Credentials are supplied only through
/// process environment variables; the normal auth API is used without remembering credentials.</summary>
internal static class LiveFusedApplicationReview
{
    public static void Run(string[] args)
    {
        AppBuilder.Configure<RelaxKonOS.Client.App>().UsePlatformDetect()
            .AfterSetup(_ => Dispatcher.UIThread.Post(async () => await OpenAsync()))
            .StartWithClassicDesktopLifetime(args);
    }

    private static async Task OpenAsync()
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
            Console.Error.WriteLine($"LIVE REVIEW FAILED: {ex}");
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(1);
        }
    }
}
