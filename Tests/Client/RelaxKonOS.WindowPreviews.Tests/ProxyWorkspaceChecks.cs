using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Proxy;
using RelaxKonOS.Client.Apps.Proxy.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;

/// <summary>
/// Renders the real proxy workspace from a local fixture and checks the two things a screenshot
/// cannot prove on its own: that every page keeps its navigation state, and that the text roles this
/// workspace relies on clear 4.5:1 in the resolved palette. The screenshots are the visual record.
/// </summary>
internal static class ProxyWorkspaceChecks
{
    public static void Run(ShellSettings settings)
    {
        var original = settings.ToPreferences();
        try
        {
            VerifyTextRoleContrast();
            var directory = Path.Combine(AppContext.BaseDirectory, "preview-qa", "proxy-manager");
            Directory.CreateDirectory(directory);

            foreach (var dark in new[] { false, true })
            foreach (var language in new[] { "zh-CN", "en-US" })
            {
                settings.SystemStyleId = SystemStyleIds.WindowsLike;
                settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
                settings.Language = language;
                Pump();
                var suffix = $"{language}-{(dark ? "dark" : "light")}";

                // A fresh workspace per appearance: a window reused across a theme switch can capture
                // its own re-layout, which says nothing about what a user sees.
                var model = new ProxyManagerViewModel(new ProxyRepositoryFixture(), canManage: true, canManageTun: true);
                model.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                Check(model.Runtime?.State == ProxyRuntimeState.Running, "The fixture runtime is running, so the overview renders the live state.");
                Check(model.Groups.Count == 2 && model.Subscriptions.Count == 3 && model.Connections.Count == 4, "The fixture exposes groups, subscriptions, connections and logs.");
                model.Groups[0].IsExpanded = true;

                var workspace = new ProxyManagerWorkspace(model);
                var window = new Window { Width = 1180, Height = 760, Content = workspace };
                window.Show();
                Pump();
                Pump();

                foreach (var (section, page, name) in Pages())
                {
                    Select(workspace, section);
                    Pump();
                    Check(ReferenceEquals(workspace.FindControl<ContentControl>("ContentHost")!.Content?.GetType(), page),
                        $"{section}: navigation did not create {page.Name}.");
                    CheckLocalized(workspace, $"{section}/{suffix}");
                    Capture(directory, $"proxy-{name}-{suffix}", window);
                }

                // The overview is a scrolling page; the tall frame is the one that shows all of it.
                window.Height = 1120;
                Pump();
                Select(workspace, "overview");
                Pump();
                Check(CardPairs(workspace).All(pair => pair.ColumnDefinitions.Count == 2),
                    "A wide page keeps the card pairs side by side.");
                Capture(directory, $"proxy-overview-tall-{suffix}", window);

                // The narrow layout drops the rail for a menu; the page itself must keep its width.
                window.Height = 760;
                window.Width = 420;
                Pump();
                Select(workspace, "overview");
                Pump();
                var menu = workspace.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CompactWorkspaceMenu");
                Check(menu.IsVisible && workspace.Bounds.Width > 300, "A narrow workspace uses the compact page menu instead of clipping the page.");
                menu.Flyout!.ShowAt(menu);
                Pump();
                var pages = ((MenuFlyout)menu.Flyout).Items.OfType<MenuItem>().ToArray();
                Check(pages.Length == 6 && pages.All(item => item.Header is string { Length: > 0 }),
                    "The compact menu still lists every page by its localized label after the rail gained icons.");
                ((MenuFlyout)menu.Flyout).Hide();
                Pump();
                Check(CardPairs(workspace).All(pair => pair.ColumnDefinitions.Count == 1 && pair.RowDefinitions.Count == 2),
                    "A narrow page stacks its card pairs into one column instead of crushing two cards.");
                Capture(directory, $"proxy-overview-compact-{suffix}", window);

                Select(workspace, "connections");
                Pump();
                Capture(directory, $"proxy-connections-compact-{suffix}", window);
                Select(workspace, "settings");
                Pump();
                Capture(directory, $"proxy-settings-compact-{suffix}", window);
                Select(workspace, "overview");
                Pump();

                Select(workspace, "settings");
                Pump();
                Check(workspace.FindControl<Button>("SettingsButton")!.Classes.Contains("nav-selected")
                    && !workspace.FindControl<Button>("OverviewButton")!.Classes.Contains("nav-selected"),
                    "Only the current page carries the selected navigation state.");
                window.Close();
                Pump();

                if (language != "zh-CN") continue;
                foreach (var tun in new[] { false, true })
                {
                    // The footer handle is only dereferenced by the save/cancel commands, which this
                    // render does not invoke; the view itself renders from the view model alone.
                    var dialogModel = new ProxyManagerViewModel(new ProxyRepositoryFixture(), canManage: true, canManageTun: true);
                    dialogModel.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                    dialogModel.SelectNetworkSettingsPageForDialog(tun);
                    var dialogWindow = new Window { Width = 600, Height = tun ? 700 : 650, Content = new ProxyNetworkSettingsDialogView(dialogModel, null!) };
                    dialogWindow.Show();
                    Pump();
                    Pump();
                    CheckLocalized(dialogWindow, $"network-settings/{suffix}");
                    Capture(directory, $"proxy-network-settings-{(tun ? "tun" : "system")}-{suffix}", dialogWindow);
                    dialogWindow.Close();
                    Pump();
                }
            }

            Console.WriteLine($"Proxy workspace checks passed; screenshots in {directory}.");
        }
        finally
        {
            settings.Apply(original);
            Pump();
        }
    }

    private static IEnumerable<(string Section, Type Page, string Name)> Pages() =>
    [
        ("overview", typeof(ProxyOverviewView), "overview"),
        ("profiles", typeof(ProxyProfilesView), "subscriptions"),
        ("proxies", typeof(ProxyGroupsView), "groups"),
        ("connections", typeof(ProxyConnectionsView), "connections"),
        ("logs", typeof(ProxyLogsView), "logs"),
        ("settings", typeof(ProxySettingsView), "settings"),
    ];

    private static IEnumerable<Grid> CardPairs(Control view) =>
    [
        view.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == "OverviewPairSubscriptions"),
        view.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == "OverviewPairNetwork"),
        view.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == "OverviewPairFacts"),
    ];

    private static void Select(ProxyManagerWorkspace workspace, string section)
    {
        var button = workspace.GetVisualDescendants().OfType<Button>().First(candidate => candidate.Tag as string == section);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>The headless compositor is the same frame a user sees, including text options.</summary>
    private static void Capture(string directory, string name, Window window)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException($"{name}: the headless window produced no frame.");
        frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>No visible label may fall back to its resource key.</summary>
    private static void CheckLocalized(Control view, string where)
    {
        foreach (var text in view.GetVisualDescendants().OfType<TextBlock>().Where(candidate => candidate.IsEffectivelyVisible))
        {
            if (string.IsNullOrEmpty(text.Text)) continue;
            Check(!text.Text.Contains("proxy.", StringComparison.Ordinal) && !text.Text.Contains("common.", StringComparison.Ordinal),
                $"{where}: unresolved resource key '{text.Text}'.");
        }
    }

    /// <summary>
    /// The workspace states measured facts in the secondary role and pill labels in the primary role.
    /// Both choices exist because the accent-on-tint and tertiary combinations they replaced sat under
    /// the 4.5:1 floor; this keeps that decision from silently regressing in any built-in palette.
    /// </summary>
    private static void VerifyTextRoleContrast()
    {
        foreach (var dark in new[] { false, true })
        {
            var palette = ThemePaletteDefaults.Resolve(AppearancePreferencesDto.Default, dark);
            var mode = dark ? "dark" : "light";
            foreach (var (foreground, background) in new[]
            {
                ("TextSecondary", "SurfaceRaised"), ("TextSecondary", "SurfaceSunken"),
                ("TextPrimary", "SelectionBackground"), ("TextPrimary", "SuccessMuted"),
                ("TextPrimary", "WarningMuted"), ("TextPrimary", "AccentMuted"),
                ("TextOnAccent", "Accent"), ("Danger", "SurfaceRaised"),
            })
            {
                var ratio = Contrast(palette[foreground], palette[background]);
                Check(ratio >= 4.5, $"{mode}: {foreground} on {background} is only {ratio:0.00}:1.");
            }

            // The rejected combination stays measured so the reason for the new roles is recorded.
            var accentOnTint = Contrast(palette["Accent"], palette["SelectionBackground"]);
            Console.WriteLine($"{mode}: accent on its own selection tint is {accentOnTint:0.00}:1, which is why selected navigation text uses TextPrimary.");
        }
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string color)
        {
            static double Channel(string hex)
            {
                var value = Convert.ToInt32(hex, 16) / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(color[^6..^4]) + 0.7152 * Channel(color[^4..^2]) + 0.0722 * Channel(color[^2..]);
        }

        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Local fixture: real contract records, no server and no host mutation.</summary>
    private sealed class ProxyRepositoryFixture : IProxyRepository
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Now;
        private static readonly Guid ProfileId = Guid.NewGuid();

        private static readonly ProxyEngineCapabilities Engine = new(true, true, true, true, true, true);
        private static readonly ProxyPlatformCapabilities Platform = new(true, true, false, true, true, false,
            new ProxySystemProxyCapabilities(true, true, false, true));
        private static readonly ProxyRuntimeDto Runtime = new("mihomo", ProxyRuntimeMode.Managed, ProxyRuntimeState.Running,
            "1.19.2", "1.19.1", true, false);
        private static readonly ProxyHealthDto Healthy = new(ProxyRuntimeState.Running, ProxyTunState.Disabled,
            ProxyHealthState.Healthy, true, true, true);

        private static readonly ProxySubscriptionDto[] SubscriptionList =
        [
            new(Guid.NewGuid(), "机场 · 主订阅", ProfileId, true, Now.AddMinutes(-12), Now.AddDays(-90), Now.AddMinutes(-12)),
            new(Guid.NewGuid(), "备用线路", ProfileId, false, Now.AddDays(-3), Now.AddDays(-40), Now.AddDays(-3)),
            new(Guid.NewGuid(), "实验节点", ProfileId, false, Now.AddDays(-9), Now.AddDays(-20), Now.AddDays(-9)),
        ];

        public Task<ProxyOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyOverviewDto("mihomo", Engine, Platform, Runtime, Healthy, ProxyOperatingMode.ListenerOnly,
                new ProxyProfileDto(ProfileId, "默认档案", "mihomo", true, 12, Now.AddDays(-90), Now.AddMinutes(-12)),
                18, new ProxyRecoveryStatusDto(false, false, null), "Windows 11 Pro 24H2 · 26100.2314"));

        public Task<IReadOnlyList<ProxyProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProxyProfileDto>>(
            [
                new(ProfileId, "默认档案", "mihomo", true, 12, Now.AddDays(-90), Now.AddMinutes(-12)),
                new(Guid.NewGuid(), "实验档案", "mihomo", false, 4, Now.AddDays(-30), Now.AddDays(-2)),
            ]);

        public Task<IReadOnlyList<ProxySubscriptionDto>> ListSubscriptionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProxySubscriptionDto>>(SubscriptionList);

        public Task<ProxySubscriptionDownloadOptionsDto> GetSubscriptionDownloadOptionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxySubscriptionDownloadOptionsDto(true));

        public Task<ProxySubscriptionDto> ImportSubscriptionAsync(ImportProxySubscriptionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(SubscriptionList[0]);

        public Task<ProxySubscriptionContentDto> GetSubscriptionContentAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxySubscriptionContentDto(subscriptionId, "mixed-port: 7890\nmode: rule", Now));

        public Task<ProxyOperationAcceptedDto> RefreshAllSubscriptionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyOperationAcceptedDto(Guid.NewGuid()));

        public Task<ProxyOperationAcceptedDto> ActivateSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyOperationAcceptedDto(Guid.NewGuid()));

        public Task<IReadOnlyList<ProxyGroupDto>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProxyGroupDto>>(
            [
                new("节点选择", "select", "香港 01", ["香港 01", "香港 02", "日本 03", "新加坡 04", "美国 05", "德国 06"]),
                new("自动选择", "url-test", "日本 03", ["香港 01", "香港 02", "日本 03", "新加坡 04"]),
            ]);

        public Task<ProxyControllerConfigurationDto> GetControllerConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyControllerConfigurationDto(new ProxyRoutingModeDto(ProxyRoutingMode.Rule),
                new ProxyDnsStatusDto(true, false, "fake-ip")));

        public Task SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ProxyDelayDto> TestProxyDelayAsync(string groupName, string proxyName, TestProxyDelayRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyDelayDto(proxyName, 42, false));

        public Task<IReadOnlyList<ProxyConnectionDto>> ListConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProxyConnectionDto>>(
            [
                new("1", "tcp", "127.0.0.1:51322", "api.github.com:443", "Match", "节点选择 → 香港 01", Now.AddMinutes(-1)),
                new("2", "tcp", "127.0.0.1:51401", "cdn.jsdelivr.net:443", "Match", "节点选择 → 香港 01", Now.AddMinutes(-2)),
                new("3", "udp", "127.0.0.1:51402", "1.1.1.1:53", "DNS", "DIRECT", Now.AddMinutes(-3)),
                new("4", "tcp", "127.0.0.1:51488", "packages.microsoft.com:443", "Match", "自动选择 → 日本 03", Now.AddMinutes(-4)),
            ]);

        public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyTrafficDto(1_240_000, 8_460_000, 3_180_000_000, 41_600_000_000, 96_400_000));

        public Task<IReadOnlyList<ProxyLogEntryDto>> ListLogsAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProxyLogEntryDto>>(
            [
                new(Now.AddSeconds(-4), "info", "Rule matched: Match → 节点选择[香港 01]"),
                new(Now.AddSeconds(-9), "warning", "DNS query exceeded 2000ms for example.org, falling back to DIRECT"),
                new(Now.AddSeconds(-15), "info", "Tunnel connection established to api.github.com:443"),
                new(Now.AddSeconds(-22), "error", "Controller request failed once; retrying with the configured backoff"),
                new(Now.AddSeconds(-30), "info", "Subscription refresh completed"),
            ]);

        public Task<ProxySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxySettingsDto(true, false, true, false, true, "warning", 7890, false, "127.0.0.1",
                ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default));

        public Task UpdateSettingsAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RuntimeResourceSnapshot> GetResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceSnapshot(Guid.NewGuid(),
            [
                new RuntimeResourceObservation("geoip.metadb", RuntimeResourceState.Verified, 8_912_384, "0F1E2D"),
                new RuntimeResourceObservation("geosite.dat", RuntimeResourceState.Verified, 4_112_640, "3A4B5C"),
            ]));

        public Task<RuntimeResourceReport> PrepareResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceReport(Guid.NewGuid(), []));

        public Task<ProxyRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Runtime);

        public Task<ProxyOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProxyOperationDto?>(null);

        public Task<ProxyOperationAcceptedDto> LifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyOperationAcceptedDto(Guid.NewGuid()));

        public Task<ProxyOperationAcceptedDto> EnableTunAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyOperationAcceptedDto(Guid.NewGuid()));

        public Task<ProxyOperationAcceptedDto> DisableTunAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyOperationAcceptedDto(Guid.NewGuid()));

        public Task<ProxyOperationAcceptedDto> EmergencyDisableTunAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyOperationAcceptedDto(Guid.NewGuid()));

        public Task<ProxyProfileDto> CreateProfileAsync(string name, string engineId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyProfileDto(Guid.NewGuid(), name, engineId, false, 1, Now, Now));

        public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ProxyProfileDto> ActivateProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyProfileDto(profileId, "默认档案", "mihomo", true, 13, Now.AddDays(-90), Now));

        public Task SelectGroupAsync(string groupName, string proxy, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
