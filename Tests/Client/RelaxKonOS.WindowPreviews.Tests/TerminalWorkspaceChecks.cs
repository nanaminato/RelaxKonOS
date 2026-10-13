using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System.Text.Json;
using RelaxKonOS.Client.Apps.Terminal;
using RelaxKonOS.Protocol.Workspace;

internal static class TerminalWorkspaceChecks
{
    public static void Run()
    {
        var workspace = new TerminalWorkspaceView();
        var first = new TerminalViewModel(null, new Settings(), null);
        var second = new TerminalViewModel(null, new Settings(), null);
        first.Title = @"C:\WINDOWS\system32\WindowsPowerShell\v1.0\powershell.exe";
        Check(first.DisplayTitle == "powershell.exe", "Executable paths should have compact labels while preserving their full title.");
        first.Title = "user@host: ~/project";
        Check(first.DisplayTitle == first.Title, "Shell-provided titles must remain intact.");
        var firstView = new TerminalView { DataContext = first };
        Check(firstView.FindControl<Button>("SessionMenuButton")!.Flyout is MenuFlyout menu && menu.Items.Count >= 7,
            "The named session menu exposes editing and settings actions.");
        var secondView = new TerminalView { DataContext = second };
        workspace.AddTab(first, firstView);
        workspace.AddTab(second, secondView);
        var strip = workspace.FindControl<ListBox>("TabStrip")!;
        var host = workspace.FindControl<Grid>("SessionHost")!;
        Check(strip.SelectedItem == second && !firstView.IsVisible && secondView.IsVisible, "New tab selection");
        var originalParent = firstView.Parent;
        strip.SelectedItem = first;
        Check(firstView.IsVisible && !secondView.IsVisible && firstView.Parent == originalParent && host.Children.Count == 2,
            "Switching must retain both session controls, without unloading a PTY");
        workspace.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Tab, KeyModifiers = KeyModifiers.Control });
        Check(strip.SelectedItem == second, "Ctrl+Tab switches sessions");
        workspace.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Tab, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift });
        Check(strip.SelectedItem == first, "Ctrl+Shift+Tab switches back");
        var requested = 0;
        workspace.RequestNewTab = () => requested++;
        workspace.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.T, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift });
        Check(requested == 1 && workspace.Tabs.Count == 2, "New tab shortcut delegates to the app");
        TerminalViewModel? closing = null;
        workspace.RequestCloseTabAsync = model => { closing = model; return Task.CompletedTask; };
        workspace.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.W, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift });
        Check(closing == first && workspace.Tabs.Count == 2, "Close shortcut must request confirmation before removing a tab");
        workspace.RemoveTab(first);
        Check(workspace.Tabs.Count == 1 && strip.SelectedItem == second && secondView.IsVisible
            && host.Children.Count == 1 && firstView.Parent is null, "Close only the requested session and select its neighbor");
        Check(!first.HasLiveSession && !second.HasLiveSession, "Unattached test views must not start processes");
        var addButton = workspace.FindControl<Button>("AddTabButton")!;
        var scroller = workspace.FindControl<ScrollViewer>("TabScroller")!;
        Check(addButton.Parent is StackPanel { } row && ReferenceEquals(row.Parent, scroller) && row.Children.Count == 2
            && ReferenceEquals(row.Children[0], strip) && ReferenceEquals(row.Children[1], addButton),
            "The new-tab command follows the last tab inside the strip's own scroll row");

        // A strip that outgrows its window scrolls sideways instead of squeezing every session.
        var crowded = new TerminalWorkspaceView();
        var preview = new Window { Width = 480, Height = 240, Content = crowded };
        preview.Show();
        Dispatcher.UIThread.RunJobs();
        for (var index = 0; index < 6; index++)
        {
            var model = new TerminalViewModel(null, new Settings(), null);
            crowded.AddTab(model, new TerminalView { DataContext = model });
        }
        Dispatcher.UIThread.RunJobs();
        var crowdedScroller = crowded.FindControl<ScrollViewer>("TabScroller")!;
        var crowdedStrip = crowded.FindControl<ListBox>("TabStrip")!;
        var picker = (crowded.FindControl<Button>("TabPickerButton")!.Flyout as Flyout)?.Content as ListBox;
        Check(picker is not null && ReferenceEquals(picker.ItemsSource, crowded.Tabs),
            "The overflow list enumerates the same sessions as the strip");
        Check(crowdedScroller.Extent.Width > crowdedScroller.Viewport.Width,
            "A crowded strip overflows instead of squeezing every session");
        Check(crowdedScroller.Offset.X > 0, "The active session stays in view after the strip grows");
        var atEnd = crowdedScroller.Offset.X;
        picker!.SelectedItem = crowded.Tabs[2];
        Dispatcher.UIThread.RunJobs();
        Check(crowdedStrip.SelectedItem == crowded.Tabs[2], "Choosing a session from the overflow list activates it");
        Check(crowdedScroller.Offset.X < atEnd, "Activating an off-screen session scrolls the strip to it");
        crowdedStrip.SelectedItem = crowded.Tabs[0];
        Dispatcher.UIThread.RunJobs();
        Check(crowdedScroller.Offset.X == 0, "The leftmost session returns the strip to its start");
        Check(crowded.ScrollTabsBy(-1) && crowdedScroller.Offset.X > 0, "The wheel scrolls a crowded strip");
        Check(crowded.ScrollTabsBy(1) && crowdedScroller.Offset.X == 0, "The wheel scrolls the strip back");
        preview.Close();
        Dispatcher.UIThread.RunJobs();
        CheckBackendSelection();
        CheckOutputBuffer();
        CheckRenderingPreferences();
        Console.WriteLine("Terminal workspace checks passed (17 scenarios).");
    }

    private static void CheckRenderingPreferences()
    {
        var transport = new PresentationTransport();
        using var surface = new BuiltInTerminalSurface(new PresentationFactory(transport), new SshDesktopCredentialProvider(new RelaxKonOS.Client.Services.ServerCenter.SshDesktopSession(null!)), new RoyalTerminal.Terminal.Transport.Ssh.KnownHostsSshHostKeyValidator());
        var settings = TerminalSettingsDto.Default with { ScrollbackLines = 50, CursorStyle = "bar", CursorBlink = false };
        surface.ApplyAppearance(settings);
        var window = new Window { Width = 640, Height = 320, Content = surface.View };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var start = surface.StartSessionAsync(new RoyalTerminal.Terminal.PtyTransportOptions(null, null, null, new(80, 24, 640, 320)), CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!start.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!start.IsCompleted) throw new TimeoutException("Fake terminal transport did not start.");
        start.GetAwaiter().GetResult();
        for (var index = 0; index < 20; index++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        var cursor = surface.Control.GetType().GetField("_vtProcessor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(surface.Control) as RoyalTerminal.Terminal.ITerminalCursorStyleSource;
        Check(cursor is not null && cursor.CursorStyle == RoyalTerminal.Terminal.TerminalCursorStyle.Bar && !cursor.CursorBlinking,
            $"Built-in cursor preferences must reach the actual emulator state. source={surface.Control.TerminalSessionService.ModeSource?.GetType()} style={cursor?.CursorStyle} blink={cursor?.CursorBlinking}");
        Check(surface.Control.ScrollbackLimit == 50 && surface.Control.Screen!.ScrollbackLimit == 50,
            "Session startup must preserve the configured history limit.");
        surface.ApplyAppearance(settings with { ScrollbackLines = 0, CursorStyle = "underline", CursorBlink = true });
        for (var index = 0; index < 20; index++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Check(surface.Control.Screen!.ScrollbackLimit == 0 && cursor!.CursorStyle == RoyalTerminal.Terminal.TerminalCursorStyle.Underline && cursor.CursorBlinking,
            "Live appearance changes must affect history and cursor without restarting a transport.");
        Check(transport.Starts == 1 && transport.InputBytes == 0, "Appearance changes must never become shell input or restart a session.");
        var options = XtermJsTerminalSurface.DescribeOptions(settings with { ScrollbackLines = 0 }, new Dictionary<string, string>());
        Check((int)options["scrollback"]! == 0 && (string)options["cursorStyle"]! == "bar" && !(bool)options["cursorBlink"]!,
            "xterm.js must receive explicit zero history and disabled blinking.");
        window.Close(); surface.StopSession();
        Console.WriteLine("Terminal presentation checks passed (emulator cursor, blink, live history, no PTY input/restart).");
    }
    private sealed class PresentationFactory(PresentationTransport transport) : RoyalTerminal.Terminal.ITerminalTransportFactory
    {
        public RoyalTerminal.Terminal.ITerminalTransport Create(RoyalTerminal.Terminal.ITerminalTransportOptions options) => transport;
    }
    private sealed class PresentationTransport : RoyalTerminal.Terminal.ITerminalTransport
    {
        public int Starts; public int InputBytes;
        public bool IsRunning { get; private set; }
        public event Action<byte[], int>? DataReceived { add { } remove { } }
        public event Action<int>? ProcessExited { add { } remove { } }
        public ValueTask StartAsync(RoyalTerminal.Terminal.ITerminalTransportOptions options, CancellationToken cancellationToken) { Starts++; IsRunning = true; return ValueTask.CompletedTask; }
        public void SendInput(ReadOnlySpan<byte> data) => InputBytes += data.Length;
        public void Resize(RoyalTerminal.Terminal.TerminalSessionDimensions dimensions) { }
        public ValueTask StopAsync() { IsRunning = false; return ValueTask.CompletedTask; }
        public void Dispose() => IsRunning = false;
    }

    private static void CheckOutputBuffer()
    {
        using var surface = new XtermJsTerminalSurface(null!, new Dictionary<string, string>());
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var receive = typeof(XtermJsTerminalSurface).GetMethod("OnTransportData", flags)!;
        var take = typeof(XtermJsTerminalSurface).GetMethod("TakePending", flags)!;
        var pending = (Queue<byte>)typeof(XtermJsTerminalSurface).GetField("_pending", flags)!.GetValue(surface)!;
        const int limit = 4 * 1024 * 1024;
        var burst = new byte[limit + 17];
        burst[17] = 42;
        burst[^1] = 99;
        receive.Invoke(surface, [burst, burst.Length]);
        Check(pending.Count == limit && pending.Peek() == 42, "An oversized burst must retain the bounded tail without throwing.");
        var chunk = (byte[])take.Invoke(surface, null)!;
        Check(chunk.Length == 96 * 1024 && chunk[0] == 42, "Terminal output must drain in ordered, bounded chunks.");
        receive.Invoke(surface, [new byte[] { 1, 2, 3 }, 100]);
        var tail = pending.ToArray();
        Check(tail[^4] == 99 && tail[^1] == 3, "Subsequent output must preserve ordering and clamp the supplied count.");
        surface.Dispose();
        Dispatcher.UIThread.RunJobs();
        Check(pending.Count == 0, "Closing the surface must release queued output.");
    }

    /// <summary>
    /// A workspace stores a terminal-engine request; the machine in front of the user decides whether
    /// it can run it, because hosting a WebView is a property of that machine. These are the cases that
    /// have to stay stable: a shipped engine, a shipped engine on a host without a WebView, a workspace
    /// saved before the field existed, and a token this build does not ship.
    /// </summary>
    private static void CheckBackendSelection()
    {
        var selected = TerminalBackendSelector.Resolve(TerminalBackends.XtermJs, webViewHostAvailable: true);
        Check(selected is { Backend: TerminalBackendKind.XtermJs } && !selected.HasFallback,
            "A shipped engine runs when the host can provide a WebView");
        var blocked = TerminalBackendSelector.Resolve(TerminalBackends.XtermJs, webViewHostAvailable: false);
        Check(blocked is { Backend: TerminalBackendKind.BuiltIn, FallbackReasonKey: "terminal.backend.fallback.webview_unavailable" },
            "An engine the host cannot run falls back to the built-in terminal instead of opening a blank window");
        Check(TerminalBackendSelector.Resolve(TerminalBackends.BuiltIn, webViewHostAvailable: false).FallbackReasonKey is null,
            "The built-in terminal is never reported as a fallback");
        Check(TerminalBackendSelector.Resolve(null, webViewHostAvailable: true) is { Backend: TerminalBackendKind.BuiltIn } and { FallbackReasonKey: null },
            "A workspace saved before the backend field existed keeps the built-in terminal");
        Check(TerminalBackendSelector.Resolve("xterm-wasm", webViewHostAvailable: true)
                is { Backend: TerminalBackendKind.BuiltIn, FallbackReasonKey: "terminal.backend.fallback.unknown" },
            "An engine token this build does not ship is reported instead of silently ignored");
        Check(TerminalBackends.TryNormalize(" XTerm ", out var canonical) && canonical == TerminalBackends.XtermJs
              && TerminalBackendSelector.FromWire(canonical) == TerminalBackendKind.XtermJs,
            "A stored engine token is canonicalized before it is compared");
        Check(!TerminalBackends.TryNormalize("xterm-wasm", out _),
            "The wire contract refuses an engine token this build does not ship");

        // Every notice a user can be shown has to resolve to real text, in whichever language is active.
        var notices = new[]
        {
            TerminalBackendSelector.Resolve(TerminalBackends.XtermJs, webViewHostAvailable: false).FallbackReasonKey,
            TerminalBackendSelector.Resolve("xterm-wasm", webViewHostAvailable: true).FallbackReasonKey,
        };
        foreach (var key in notices.OfType<string>())
        {
            var text = RelaxKonOS.Client.Localization.LocalizedText.Get(key, string.Empty);
            Check(!string.IsNullOrWhiteSpace(text) && !text.Contains(key, StringComparison.Ordinal),
                $"The fallback notice '{key}' must be a resource string, not a raw key");
        }

        var model = new TerminalViewModel(null, new Settings(), null);
        Check(model.IsBuiltInBackendSelected && !model.IsXtermBackendSelected,
            "The terminal settings show the engine the workspace stored");
        Check(model.IsXtermBackendAvailable == TerminalBackendSelector.WebViewHostAvailable,
            "The terminal settings disable an engine this host cannot run");
        Check(!model.HasBackendNotice, "A workspace that got the engine it asked for shows no fallback notice");
        model.Backend = TerminalBackends.XtermJs;
        Check(model.IsXtermBackendSelected && !model.IsBuiltInBackendSelected,
            "Choosing an engine in the settings switches the selected backend");
        Console.WriteLine("Terminal backend selection checks passed (12 scenarios).");
        CheckXtermPage();
    }

    /// <summary>
    /// The xterm.js engine is a document string handed to the platform WebView, so a broken template or
    /// an inlined option bag is invisible until someone opens a terminal on a machine that has a
    /// WebView. These checks pin the properties the document has to satisfy on every platform: fully
    /// self-contained, no leftover placeholder, a parseable option bag, and no way for session text to
    /// escape the script element it is inlined into.
    /// </summary>
    private static void CheckXtermPage()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["copy"] = "Copy" };
        var benign = XtermTerminalPage.Compose(XtermJsTerminalSurface.DescribeOptions(TerminalSettingsDto.Default, labels));
        Check(!benign.Contains("__RELAXKONOS_", StringComparison.Ordinal),
            "Every placeholder in the terminal page template must be substituted before the page is handed to a WebView");
        Check(benign.Contains("FitAddon", StringComparison.Ordinal) && benign.Contains("window.rkTerminal", StringComparison.Ordinal),
            "The document must carry the inlined fit addon and bridge");
        Check(!benign.Contains("unpkg", StringComparison.Ordinal) && !benign.Contains("cdn.", StringComparison.Ordinal)
              && !benign.Contains("src=\"http", StringComparison.Ordinal) && !benign.Contains("href=\"http", StringComparison.Ordinal),
            "The document must stay offline: no CDN, no fetched script or stylesheet");
        Check(XtermTerminalPage.BaseUri.Scheme == Uri.UriSchemeHttps,
            "The document claims a stable origin even though it never fetches anything");

        using var options = JsonDocument.Parse(OptionsLiteral(benign));
        var root = options.RootElement;
        Check(root.TryGetProperty("fontFamily", out _) && root.TryGetProperty("labels", out _)
              && root.TryGetProperty("theme", out _) && root.TryGetProperty("scrollback", out _),
            "The page must be told how to look and which labels to draw before the session attaches");
        Check(root.TryGetProperty("fontSize", out var size)
              && Math.Abs(size.GetDouble() - TerminalSettingsDto.Default.FontSize) < 0.001,
            "The inlined options must carry the workspace font size, not a hardcoded default");
        Check(root.TryGetProperty("theme", out var theme)
              && theme.GetProperty("background").GetString() == TerminalSettingsDto.Default.BackgroundColor
              && theme.GetProperty("foreground").GetString() == TerminalSettingsDto.Default.ForegroundColor,
            "The inlined options must carry the workspace colours");
        Check(root.TryGetProperty("labels", out var inlined) && inlined.GetProperty("copy").GetString() == "Copy",
            "The in-page context menu must use the host's localized labels");

        // Text that reaches the page is inlined into a script element, so a shell or a translation that
        // happens to contain HTML must not be able to end that element or break the literal it sits in.
        var hostile = XtermJsTerminalSurface.DescribeOptions(TerminalSettingsDto.Default,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["copy"] = "</script><script>alert(1)</script>\u2028" });
        var escaped = XtermTerminalPage.Compose(hostile);
        Check(Occurrences(escaped, "</script>") == Occurrences(benign, "</script>") && !escaped.Contains('\u2028'),
            "Text reaching the page must not be able to close the script element it is inlined into");

        var changed = XtermTerminalPage.Compose(XtermJsTerminalSurface.DescribeOptions(
            TerminalSettingsDto.Default with { Backend = TerminalBackends.XtermJs, FontSize = 22 }, labels));
        Check(!string.Equals(changed, benign, StringComparison.Ordinal) && changed.Contains("FitAddon", StringComparison.Ordinal),
            "Composing a page twice with different options must not hand back a stale document");
        Console.WriteLine("Terminal page composition checks passed (10 scenarios).");
    }

    /// <summary>Reads back the option literal the page was seeded with, exactly as the browser would.</summary>
    private static string OptionsLiteral(string document)
    {
        const string marker = "window.__relaxkonosTerminal = ";
        var start = document.IndexOf(marker, StringComparison.Ordinal);
        Check(start >= 0, "The page must seed its options from the host");
        start += marker.Length;
        var end = document.IndexOf("</script>", start, StringComparison.Ordinal);
        Check(end > start, "The seeded options must be terminated");
        return document[start..end].Trim().TrimEnd(';');
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Settings : ITerminalSettingsClient
    {
        public Task<TerminalSettingsDto> GetAsync(string serverUrl, string accessToken, Guid workspaceId, CancellationToken ct = default)
            => Task.FromResult(TerminalSettingsDto.Default);
        public Task<TerminalSettingsDto> SaveAsync(string serverUrl, string accessToken, Guid workspaceId, TerminalSettingsDto settings, CancellationToken ct = default)
            => Task.FromResult(settings);
    }
}
