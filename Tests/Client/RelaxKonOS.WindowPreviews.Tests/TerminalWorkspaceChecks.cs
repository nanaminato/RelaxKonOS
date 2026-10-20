using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        CheckEngineMigration();
        CheckRenderingPreferences();
        Console.WriteLine("Terminal workspace checks passed (tabs, overflow, single-engine migration, presentation).");
    }

    private static void CheckRenderingPreferences()
    {
        var transport = new PresentationTransport();
        using var surface = new BuiltInTerminalSurface(new PresentationFactory(transport), new SshDesktopCredentialProvider(new RelaxKonOS.Client.Services.ServerCenter.SshDesktopSession(null!, new RelaxKonOS.Client.Services.ShellSettings(null!))), new RoyalTerminal.Terminal.Transport.Ssh.KnownHostsSshHostKeyValidator());
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

    /// <summary>
    /// A workspace may still store the terminal engine it was written with. RoyalTerminal is the only
    /// desktop engine, so the stored request must never fail a window, the factory may not accept an
    /// engine choice, and the settings surface may not offer a picker.
    /// </summary>
    private static void CheckEngineMigration()
    {
        // The protocol still reports what the document asks for; an older document stays readable.
        Check(TerminalSettingsDto.Default.RequestedBackend == TerminalBackends.BuiltIn,
            "The default workspace settings ask for the built-in engine");
        Check((TerminalSettingsDto.Default with { Backend = TerminalBackends.XtermJs }).RequestedBackend == TerminalBackends.XtermJs,
            "The wire contract still canonicalizes the legacy xterm.js token");
        Check((TerminalSettingsDto.Default with { Backend = "  XTerm  " }).RequestedBackend == TerminalBackends.XtermJs,
            "A stored engine token is canonicalized before it is reported");
        Check((TerminalSettingsDto.Default with { Backend = "xterm-wasm" }).RequestedBackend == TerminalBackends.BuiltIn,
            "A token this build does not ship degrades to the built-in request instead of throwing");
        Check((TerminalSettingsDto.Default with { Backend = null }).RequestedBackend == TerminalBackends.BuiltIn,
            "A workspace saved before the field existed keeps the built-in engine");
        Check(TerminalBackends.All.Contains(TerminalBackends.XtermJs),
            "The shipped token set keeps the legacy value so an older document is still accepted");
        Check(!TerminalBackends.TryNormalize("xterm-wasm", out _),
            "The wire contract still refuses an engine token this build does not ship");

        // The desktop does not consume the request: it renders RoyalTerminal either way and rewrites
        // the stored token on the next appearance change, so a legacy document migrates by itself.
        var migrated = new TerminalViewModel(null, new Settings(), null);
        migrated.Appearance = TerminalSettingsDto.Default with { Backend = TerminalBackends.XtermJs };
        migrated.FontSize = TerminalSettingsDto.Default.FontSize + 1;
        Check(migrated.Appearance.Backend == TerminalBackends.BuiltIn,
            "Saving an appearance change must rewrite a stored legacy token to the built-in engine");

        // The engine is a compile-time fact rather than a runtime choice: the factory takes no
        // argument and the desktop assembly ships no second surface.
        Check(typeof(TerminalSurfaceFactory).GetMethod(nameof(TerminalSurfaceFactory.Create))?.GetParameters().Length == 0,
            "The surface factory must not accept an engine choice");
        Check(typeof(TerminalSurfaceFactory).Assembly.GetTypes()
                .All(type => !type.Name.Contains("xterm", StringComparison.OrdinalIgnoreCase)),
            "The desktop client must not ship an xterm.js surface");

        // The view-model exposes no engine API, so no view can bind the picker that no longer exists.
        foreach (var member in new[] { "Backend", "EffectiveBackend", "BackendNotice", "HasBackendNotice", "IsXtermBackendSelected", "IsXtermBackendAvailable" })
        {
            Check(typeof(TerminalViewModel).GetProperty(member) is null,
                $"The terminal view-model must not expose the retired '{member}' engine member");
        }

        var settings = new TerminalSettingsView { DataContext = new TerminalViewModel(null, new Settings(), null) };
        var host = new Window { Width = 460, Height = 420, Content = settings };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        Check(!settings.GetVisualDescendants().OfType<RadioButton>().Any(),
            "The terminal settings must not offer a terminal-engine picker");
        Check(settings.GetVisualDescendants().OfType<ComboBox>().Count() == 3,
            "The terminal settings still expose font family, size and colour scheme");
        host.Close();
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine("Terminal engine migration checks passed (single engine, legacy token tolerated).");
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
