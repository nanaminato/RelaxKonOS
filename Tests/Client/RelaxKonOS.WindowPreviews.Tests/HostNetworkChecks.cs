using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Apps.Settings.Views.Pages;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using System.Reflection;

internal static class HostNetworkChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization)
    {
        var originalLanguage = settings.Language;
        var service = new NetworkService();
        using var vm = new HostNetworkEditorViewModel(service, DispatchProxy.Create<IAuthSession, LanguageSessionStub>(), localization);
        vm.ReloadAsync().GetAwaiter().GetResult();
        Check(vm.Adapters.Count == 2 && vm.SelectedAdapter!.Name == "Ethernet 2", "Remote adapters rendered");
        vm.Dhcp = false; vm.Address = "invalid";
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(vm.HasProblem && service.Writes == 0, "Invalid address cannot reach remote service");
        vm.Address = "192.168.20.20";
        vm.RequestAuthorizationAsync = _ => Task.FromResult(false);
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(service.Writes == 0 && !vm.HasPendingConfirmation, "Cancelled authorization preserves form");
        vm.RequestAuthorizationAsync = _ => Task.FromResult(true);
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(service.Writes == 1 && vm.HasPendingConfirmation && !vm.CanEdit, "Applied change awaits connectivity confirmation");
        vm.ConfirmCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(service.ConfirmId == service.Request!.OperationId && !vm.HasPendingConfirmation && vm.CanEdit, "Confirm uses the submitted operation");
        service.FailWrite = true;
        vm.Dhcp = false; vm.Address = "192.168.20.30";
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(vm.HasPendingConfirmation && vm.HasProblem && !vm.ApplyCommand.CanExecute(null), "Lost response retains operation without replay");
        vm.ConfirmCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        var opened = false;
        vm.RequestOpenAdapter = () => opened = true;
        vm.OpenAdapter(vm.Adapters[0]);
        Check(opened, "Adapter card did not request a detail route");
        var view = new HostNetworkView { DataContext = vm };
        var scroll = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var window = new Window { Content = scroll, Width = 900, Height = 850 };
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "settings");
        Directory.CreateDirectory(output);
        window.Show();
        try
        {
            foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                settings.Language = culture;
                Check(localization.Get("common.apply", "missing") != "missing", "Missing translated Apply label");
                foreach (var size in new[] { new PixelSize(900, 850), new PixelSize(320, 480) })
                {
                    window.Width = size.Width; window.Height = size.Height; scroll.Offset = default;
                    Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, "Remote network page requires horizontal scrolling");
                    using var frame = window.CaptureRenderedFrame() ?? throw new Exception("Network page did not render");
                    frame.Save(Path.Combine(output, $"remote-network-{culture}-{size.Width}.png"), PngBitmapEncoderOptions.Default);
                    view.IsAdapterDetailPage = true;
                    Dispatcher.UIThread.RunJobs();
                    Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, "Adapter detail requires horizontal scrolling");
                    using var detailFrame = window.CaptureRenderedFrame() ?? throw new Exception("Network details did not render");
                    detailFrame.Save(Path.Combine(output, $"remote-network-detail-{culture}-{size.Width}.png"), PngBitmapEncoderOptions.Default);
                    view.IsAdapterDetailPage = false;
                }
            }
        }
        finally { window.Close(); settings.Language = originalLanguage; }
        Console.WriteLine("PASS: Remote adapters, invalid input, authorization cancellation, confirmation, uncertain-write no replay and three-language network previews.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class NetworkService : IHostNetworkService
    {
        private readonly HostSettingsConnection _connection = new("remote-test", Guid.NewGuid(), Guid.NewGuid());
        public int Writes; public HostNetworkApplyRequest? Request; public Guid ConfirmId; public bool FailWrite;
        public HostSettingsConnection CaptureConnection() => _connection;
        public bool IsCurrent(HostSettingsConnection connection) => connection == _connection;
        public Task<HostNetworkSnapshot> ReadAsync(HostSettingsConnection connection, CancellationToken ct = default) => Task.FromResult(new HostNetworkSnapshot([
            new(Guid.NewGuid().ToString(), 2, "Ethernet 2", "Remote Ethernet controller", "ethernet", true, 1_000_000_000, "00:11:22:33:44:55", true, true,
                [new("192.168.20.10", 24), new("fe80::1234", 64)], ["192.168.20.1"], ["192.168.20.1"], "r1", true),
            new(Guid.NewGuid().ToString(), 3, "Wi-Fi", "Remote wireless controller", "wifi", false, 0, "00:11:22:33:44:66", true, true, [], [], [], "r1", false, "settings.network.read_only_platform")
        ], "remote-test"));
        public Task<HostNetworkApplyResult> ApplyAsync(HostSettingsConnection connection, HostNetworkApplyRequest request, CancellationToken ct = default)
        {
            Writes++; Request = request;
            return FailWrite ? Task.FromException<HostNetworkApplyResult>(new IOException("Response lost"))
                : Task.FromResult(new HostNetworkApplyResult(request.OperationId, DateTimeOffset.UtcNow.AddSeconds(90)));
        }
        public Task<HostNetworkConfirmed> ConfirmAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct = default)
        { ConfirmId = operationId; return Task.FromResult(new HostNetworkConfirmed(true)); }
        public Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, string? password = null, string? administratorUsername = null, CancellationToken ct = default)
            => Task.FromResult(new HostElevationResult(true));
    }
}
