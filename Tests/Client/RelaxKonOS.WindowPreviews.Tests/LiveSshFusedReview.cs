using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;

internal static class LiveSshFusedReview
{
    public static async Task RunAsync(IServiceProvider services, string host, string user, string password, CancellationToken ct)
    {
        var session = services.GetRequiredService<SshDesktopSession>();
        var target = ServerHostTargetRules.Create(host, 22, user, "Fused UI review", DateTimeOffset.UtcNow);
        try { await session.ConnectAsync(target, password, ct); }
        catch (ServerCenterHostKeyRejectedException rejected)
        {
            var approved = Environment.GetEnvironmentVariable("RK_REVIEW_SSH_FINGERPRINT");
            if (approved is null || !string.Equals(approved, rejected.Observation.Fingerprint, StringComparison.Ordinal)) throw;
            // Test-only, after explicit fingerprint approval: overlay the exact observed key in
            // memory and keep the production host-key evaluator. Never persist a test pin.
            var resolver = services.GetRequiredService<IServerCenterConnectionResolver>();
            var field = resolver.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.FieldType == typeof(ISshHostKeyTrustStore));
            var inner = (ISshHostKeyTrustStore)field.GetValue(resolver)!;
            field.SetValue(resolver, new ApprovedReviewKeyStore(inner, rejected.Observation));
            await session.ConnectAsync(target, password, ct);
        }
        password = string.Empty;
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        while (desktop.MainWindow is not RelaxKonOS.Client.Views.MainWindow) await Task.Delay(100, ct);
        var windows = services.GetRequiredService<IWindowManager>();
        while (windows.HostBounds.Width <= 0) await Task.Delay(100, ct);
        var apps = services.GetRequiredService<ApplicationManager>();
        if (!apps.Launch(new AppId("relaxkonos.server-center")) || !apps.Launch(new AppId("relaxkonos.ssh-files")))
            throw new InvalidOperationException("SSH workspace applications could not be launched.");
        ManagedWindow? fileWindow;
        while ((fileWindow = windows.Windows.SingleOrDefault(window => window.Info.OwnerAppId.Value == "relaxkonos.ssh-files")) is null)
            await Task.Delay(100, ct);
        var view = (Control)fileWindow.View.Content!;
        var viewType = view.GetType();
        Require(viewType.FullName == "RelaxKonOS.App.SshFiles.Views.SshFileBrowserView"
            && System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(viewType.Assembly)?.IsCollectible == true,
            "SSH Files must launch its installed official package.");
        const BindingFlags internalMember = BindingFlags.Instance | BindingFlags.NonPublic;
        var search = viewType.GetProperty("SearchText")!;
        while (Busy() || PathOf(ActiveTab()) == ".") await Task.Delay(100, ct);
        var first = ActiveTab();
        search.SetValue(view, "review-filter");
        Invoke("NewTab");
        var second = ActiveTab();
        await Navigate("/");
        Require(PathOf(second) == "/" && Equals(search.GetValue(view), ""), "New SSH tab did not navigate independently.");
        Invoke("SwitchTab", first);
        Require(Equals(search.GetValue(view), "review-filter") && PathOf(first) != ".", "SSH switching lost the first tab's directory/filter.");
        Invoke("SwitchTab", second);
        Require(Equals(search.GetValue(view), "") && PathOf(second) == "/", "SSH switching reused the first tab's filter.");
        Invoke("CloseTab", second);
        Require(ReferenceEquals(ActiveTab(), first), "Closing the active SSH tab did not restore its neighbor.");
        search.SetValue(view, "");
        Console.WriteLine("LIVE SSH TABS PASS: SFTP home/root reads, independent filters, switching and active-tab close.");

        var settings = services.GetRequiredService<ShellSettings>();
        var original = settings.ToPreferences();
        var output = Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "live-ssh-fused"); Directory.CreateDirectory(output);
        try
        {
            foreach (var window in windows.Windows.Where(window => window.Info.OwnerAppId.Value is "relaxkonos.ssh-files" or "relaxkonos.server-center").ToArray())
            {
                windows.Focus(window);
                foreach (var other in windows.Windows) other.View.IsVisible = ReferenceEquals(other, window);
                Require(window.View.HasTitleBarContent, "SSH application did not declare fused roles.");
                foreach (var style in new[] { SystemStyleIds.WindowsLike, SystemStyleIds.MacOsLike, SystemStyleIds.UbuntuLike })
                foreach (var width in new[] { 960, 320 })
                {
                    settings.SystemStyleId = style;
                    window.View.Width = width; window.Info.Bounds = window.Info.Bounds with { Width = width };
                    await Task.Delay(150, ct); desktop.MainWindow!.UpdateLayout();
                    using var image = new RenderTargetBitmap(new PixelSize((int)window.View.Bounds.Width, (int)window.View.Bounds.Height));
                    image.Render(window.View);
                    image.Save(Path.Combine(output, $"{window.Info.OwnerAppId.Value}-{style}-{width}.png"), new PngBitmapEncoderOptions());
                }
                Console.WriteLine($"LIVE SSH HEADER PASS: {window.Info.OwnerAppId.Value}; three recipes, normal/narrow widths.");
            }
        }
        finally { settings.Apply(original); foreach (var window in windows.Windows.ToArray()) windows.ForceClose(window); session.Disconnect(); }
        Console.WriteLine("LIVE SSH REVIEW PASS: authenticated SFTP and server-center workspace; no remote write operations.");

        bool Busy() => (bool)viewType.GetField("_busy", internalMember)!.GetValue(view)!;
        object ActiveTab() => viewType.GetProperty("ActiveTab", internalMember)!.GetValue(view)!;
        static string PathOf(object tab) => (string)tab.GetType().GetProperty("Path")!.GetValue(tab)!;
        object? Invoke(string method, params object[] arguments) =>
            viewType.GetMethod(method, internalMember)!.Invoke(view, arguments);
        async Task Navigate(string path)
        {
            await ((Task)Invoke("NavigateAsync", path, true, false)!).WaitAsync(ct);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ApprovedReviewKeyStore(ISshHostKeyTrustStore inner, ServerCenterHostKeyObservation key) : ISshHostKeyTrustStore
    {
        public async Task<IReadOnlyList<ServerHostKeyRecord>> LoadAsync(CancellationToken ct = default)
            => ServerHostTrustRules.Replace(await inner.LoadAsync(ct), new ServerHostKeyRecord(key.Host, key.Port, key.Algorithm,
                Convert.ToBase64String(key.PublicKeyBlob), key.Fingerprint, DateTimeOffset.UtcNow));
        public async Task<ServerHostKeyTrust> EvaluateAsync(ServerCenterSshEndpoint endpoint, ServerCenterHostKeyObservation observation, CancellationToken ct = default)
            => ServerHostTrustRules.Evaluate(await LoadAsync(ct), endpoint.Host, endpoint.Port, observation.Algorithm, observation.PublicKeyBlob);
        public Task TrustAsync(ServerCenterSshEndpoint endpoint, ServerCenterHostKeyObservation observation, CancellationToken ct = default) => throw new NotSupportedException("Review pins cannot be persisted.");
        public Task ForgetAsync(string host, int port, string algorithm, CancellationToken ct = default) => throw new NotSupportedException("Review cannot remove pins.");
        public Task ForgetEndpointAsync(string host, int port, CancellationToken ct = default) => throw new NotSupportedException("Review cannot remove pins.");
    }
}
