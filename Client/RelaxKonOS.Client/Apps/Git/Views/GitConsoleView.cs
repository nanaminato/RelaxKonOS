using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal sealed class GitConsoleView : UserControl
{
    private readonly TextBox _output;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status;
    private readonly Dictionary<Button, string> _toolKeys = [];
    private bool _unavailable;
    private bool _wrap;
    public event EventHandler? ClearRequested;
    public GitConsoleView()
    {
        var grid = new Grid { ColumnDefinitions = new("32,*"), RowDefinitions = new("Auto,*") };
        var tools = new StackPanel { Margin = new(1, 5), Spacing = 3 };
        Button Tool(string symbol, string key, Action action)
        {
            var button = new Button { Content = symbol, Width = 28, MinWidth = 0, Height = 26, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0) };
            ToolTip.SetTip(button, LocalizedText.Get(key)); Avalonia.Automation.AutomationProperties.SetName(button, LocalizedText.Get(key));
            button.Click += (_, _) => action(); tools.Children.Add(button); _toolKeys[button] = key; return button;
        }
        _output = new TextBox { Name = "GitConsoleOutput", IsReadOnly = true, AcceptsReturn = true, BorderThickness = new(0), Background = Brushes.Transparent,
            FontFamily = new("Cascadia Code,Consolas,Menlo,monospace"), FontSize = 12, Padding = new(9, 6), TextWrapping = TextWrapping.NoWrap };
        _scroll = new ScrollViewer { Content = _output, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Tool("↵", "git.dock.soft_wrap", () => { _wrap = !_wrap; _output.TextWrapping = _wrap ? TextWrapping.Wrap : TextWrapping.NoWrap; _scroll.HorizontalScrollBarVisibility = _wrap ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto; });
        Tool("↓", "git.dock.scroll_end", () => _scroll.ScrollToEnd());
        Tool("×", "git.dock.clear_console", () => { _output.Text = ""; ClearRequested?.Invoke(this, EventArgs.Empty); });
        _status = new TextBlock { Classes = { "subtle" }, Margin = new(10, 5), FontSize = 11, IsVisible = false };
        Grid.SetColumn(_status, 1); grid.Children.Add(_status);
        Grid.SetRowSpan(tools, 2); grid.Children.Add(tools); Grid.SetColumn(_scroll, 1); Grid.SetRow(_scroll, 1); grid.Children.Add(_scroll); Content = grid;
        RefreshLocalization();
    }
    public void ShowUnavailable(bool unavailable)
    {
        _unavailable = unavailable; _status.IsVisible = unavailable;
        _status.Text = unavailable ? LocalizedText.Get("git.dock.console_unavailable") : "";
    }
    public void RefreshLocalization()
    {
        foreach (var (button, key) in _toolKeys) { ToolTip.SetTip(button, LocalizedText.Get(key)); Avalonia.Automation.AutomationProperties.SetName(button, LocalizedText.Get(key)); }
        _output.PlaceholderText = LocalizedText.Get("git.dock.console_empty");
        ShowUnavailable(_unavailable);
    }
    public void Append(IEnumerable<GitConsoleEntryDto> entries)
    {
        ShowUnavailable(false);
        var text = _output.Text ?? "";
        foreach (var entry in entries)
            text += $"[{entry.Timestamp.ToLocalTime():HH:mm:ss}] {entry.Command}\n{entry.Output}{(entry.Output.EndsWith('\n') || entry.Output.Length == 0 ? "" : "\n")}{entry.Error}{(entry.Error.EndsWith('\n') || entry.Error.Length == 0 ? "" : "\n")}[{LocalizedText.Get("git.dock.exit_code")}: {entry.ExitCode}]\n\n";
        _output.Text = text.Length <= 256 * 1024 ? text : text[^ (256 * 1024)..];
        _scroll.ScrollToEnd();
    }
    public string Text => _output.Text ?? "";
}
