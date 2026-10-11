using Avalonia.Controls;
using Avalonia.Input;
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
        Console.WriteLine("Terminal workspace checks passed (8 scenarios).");
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
