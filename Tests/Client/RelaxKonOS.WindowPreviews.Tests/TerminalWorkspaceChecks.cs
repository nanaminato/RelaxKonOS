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
        CheckRenderingPreferences(RoyalTerminal.Terminal.VtProcessorPreference.Managed);
        if (new RoyalTerminal.Terminal.GhosttyVtProcessorProvider().IsAvailable)
            CheckRenderingPreferences(RoyalTerminal.Terminal.VtProcessorPreference.Native);
        Console.WriteLine("Terminal workspace checks passed (tabs, overflow, VT selection, presentation).");
    }

    private static void CheckRenderingPreferences(RoyalTerminal.Terminal.VtProcessorPreference preference)
    {
        var transport = new PresentationTransport { StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var surface = new BuiltInTerminalSurface(new PresentationFactory(transport), new SshDesktopCredentialProvider(new RelaxKonOS.Client.Services.ServerCenter.SshDesktopSession(null!, new RelaxKonOS.Client.Services.ShellSettings(null!))), new RoyalTerminal.Terminal.Transport.Ssh.KnownHostsSshHostKeyValidator(), preference);
        var settings = TerminalSettingsDto.Default with { ScrollbackLines = 50, CursorStyle = "bar", CursorBlink = false };
        surface.ApplyAppearance(settings);
        var window = new Window { Width = 640, Height = 320, Content = surface.View };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var start = surface.StartSessionAsync(new RoyalTerminal.Terminal.PtyTransportOptions(null, null, null, new(80, 24, 640, 320)), CancellationToken.None);
        Check(!start.IsCompleted && surface.Control.TerminalSessionService.HasActiveTransport,
            "Pending connection must protect the processor already bound to the session");
        // Network transports remain pending while the window receives its final layout.
        // Key-up resolves the processor through the session service rather than the control.
        surface.Control.Columns += 7;
        surface.Control.Rows += 3;
        surface.Control.ScrollbackLimit = 51;
        Dispatcher.UIThread.RunJobs();
        transport.StartGate.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!start.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!start.IsCompleted) throw new TimeoutException("Fake terminal transport did not start.");
        start.GetAwaiter().GetResult();
        surface.Control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
        surface.Control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Up });
        Check(transport.InputBytes > 0, "Keyboard input must still reach the transport after pending-start layout changes");
        transport.InputBytes = 0;
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
        Console.WriteLine($"Terminal {preference} presentation checks passed (cursor, blink, history, no PTY input/restart).");
    }
    private sealed class PresentationFactory(PresentationTransport transport) : RoyalTerminal.Terminal.ITerminalTransportFactory
    {
        public RoyalTerminal.Terminal.ITerminalTransport Create(RoyalTerminal.Terminal.ITerminalTransportOptions options) => transport;
    }
    private sealed class PresentationTransport : RoyalTerminal.Terminal.ITerminalTransport
    {
        public int Starts; public int InputBytes;
        public TaskCompletionSource? StartGate;
        public bool IsRunning { get; private set; }
        public event Action<byte[], int>? DataReceived { add { } remove { } }
        public event Action<int>? ProcessExited { add { } remove { } }
        public async ValueTask StartAsync(RoyalTerminal.Terminal.ITerminalTransportOptions options, CancellationToken cancellationToken)
        {
            Starts++;
            if (StartGate is not null) await StartGate.Task.WaitAsync(cancellationToken);
            IsRunning = true;
        }
        public void SendInput(ReadOnlySpan<byte> data) => InputBytes += data.Length;
        public void Resize(RoyalTerminal.Terminal.TerminalSessionDimensions dimensions) { }
        public ValueTask StopAsync() { IsRunning = false; return ValueTask.CompletedTask; }
        public void Dispose() => IsRunning = false;
    }

    /// <summary>
    /// VT selection is a device preference independent of the workspace renderer token.
    /// </summary>
    private static void CheckEngineMigration()
    {
        var provider = new RoyalTerminal.Terminal.GhosttyVtProcessorProvider();
        if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64)
            Check(provider.IsAvailable, "Ordinary Windows Debug builds must include a loadable Ghostty native library without an explicit RID");
        var factory = new RoyalTerminal.Terminal.DefaultVtProcessorFactory([provider]);
        foreach (var preference in Enum.GetValues<RoyalTerminal.Terminal.VtProcessorPreference>())
        {
            if (preference == RoyalTerminal.Terminal.VtProcessorPreference.Native && !provider.IsAvailable)
            {
                try
                {
                    factory.Create(new RoyalTerminal.Avalonia.Rendering.TerminalScreen(80, 24), preference);
                    throw new Exception("Native must fail when its library is unavailable");
                }
                catch (InvalidOperationException) { }
                continue;
            }
            var processor = factory.Create(new RoyalTerminal.Avalonia.Rendering.TerminalScreen(80, 24), preference);
            var native = preference != RoyalTerminal.Terminal.VtProcessorPreference.Managed && provider.IsAvailable;
            Check(processor.GetType().Name == (native ? "GhosttyVtProcessor" : "BasicVtProcessor"),
                $"{preference} must instantiate the expected VT processor");
            (processor as IDisposable)?.Dispose();
        }
        Console.WriteLine($"VT processor selection passed; Ghostty available: {provider.IsAvailable}");
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

        // VT preference is independent of the workspace backend token. The factory takes an
        // explicit preference and the desktop assembly ships one Avalonia surface.
        Check(typeof(TerminalSurfaceFactory).GetMethod(nameof(TerminalSurfaceFactory.Create))?.GetParameters().Single().ParameterType == typeof(RoyalTerminal.Terminal.VtProcessorPreference),
            "The surface factory must accept the RoyalTerminal VT preference");
        Check(typeof(TerminalSurfaceFactory).Assembly.GetTypes()
                .All(type => !type.Name.Contains("xterm", StringComparison.OrdinalIgnoreCase)),
            "The desktop client must not ship an xterm.js surface");

        // The removed WebView backend API stays absent.
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
            "The VT selector uses a ComboBox");
        Check(settings.GetVisualDescendants().OfType<ComboBox>().Count() == 4,
            "The terminal settings expose VT engine, font family, size and colour scheme");
        host.Close();
        Dispatcher.UIThread.RunJobs();
        var path = Path.Combine(Path.GetTempPath(), "relaxkon-vt-" + Guid.NewGuid() + ".json");
        try
        {
            var preferences = new RelaxKonOS.Client.Services.DesktopDevicePreferences(path);
            var model = new TerminalViewModel(null, new Settings(), null, devicePreferences: preferences);
            Check(model.VtEngines.SequenceEqual(new[] { "Auto", "Managed", "Native" }), "All VT modes are selectable");
            model.VtEngine = "Native";
            var reloaded = new RelaxKonOS.Client.Services.DesktopDevicePreferences(path);
            Check(reloaded.Value.TerminalVtEngine == "Native", "VT preference survives device settings reload");
            Check(new TerminalViewModel(null, new Settings(), null, devicePreferences: reloaded).VtEngine == "Native",
                "New terminal reads the saved VT preference");
        }
        finally { File.Delete(path); }
        Console.WriteLine("Terminal VT selection and persistence checks passed.");
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
