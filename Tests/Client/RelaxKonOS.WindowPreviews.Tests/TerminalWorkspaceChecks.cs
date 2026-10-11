using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Terminal;
using RelaxKonOS.Protocol.Workspace;

internal static class TerminalWorkspaceChecks
{
    public static void Run()
    {
        var workspace = new TerminalWorkspaceView();
        var first = new TerminalViewModel(null, new Settings(), null);
        var second = new TerminalViewModel(null, new Settings(), null);
        var firstView = new TerminalView { DataContext = first };
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
        Console.WriteLine("Terminal workspace checks passed (17 scenarios).");
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
