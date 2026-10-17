using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.ProxyServer.Views;

/// <summary>Proxy Server workspace with compact page navigation and a pinned publication bar.</summary>
public partial class ProxyServerWorkspace : UserControl
{
    private const int PageCount = 5;
    private ProxyServerViewModel? _viewModel;

    public ProxyServerWorkspace()
    {
        InitializeComponent();
        RelaxKonOS.Client.Controls.CompactWorkspaceNavigation.Install(this, WorkspaceBody);
        ShowPage(0);
    }

    public ProxyServerWorkspace(ProxyServerViewModel viewModel) : this() => DataContext = viewModel;

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var page)) ShowPage(page);
    }

    private void ShowPage(int page)
    {
        for (var i = 0; i < PageCount; i++)
        {
            this.FindControl<StackPanel>($"Page{i}")!.IsVisible = i == page;
            this.FindControl<Button>($"PageButton{i}")!.Classes.Set("nav-selected", i == page);
        }
        BodyScroll.Offset = default;
    }

    /// <summary>Copies the visible export text. The view owns the clipboard; the view model keeps no copy.</summary>
    private async void CopyExport_Click(object? sender, RoutedEventArgs e)
        => await CopyAsync(_viewModel?.ExportText);

    private async void CopyToken_Click(object? sender, RoutedEventArgs e)
        => await CopyAsync(_viewModel?.IssuedUrls);

    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    /// <summary>Moves the identity and refresh command into the host window's title bar.</summary>
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as ProxyServerViewModel;
    }
}
