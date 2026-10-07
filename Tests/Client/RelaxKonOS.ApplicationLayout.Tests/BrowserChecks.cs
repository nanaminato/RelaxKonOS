using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Client.Apps.Browser.ViewModels;
using RelaxKonOS.Client.Apps.Browser.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Browser;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;

internal static class BrowserChecks
{
    public static void Run(Window host, ShellSettings settings, AppearanceService appearance, string output)
    {
        foreach (var count in new[] { 1000, 10000 })
        {
            var capacityModel = new BrowserViewModel(new BrowserFixtureClient(count, count));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            capacityModel.LoadAsync().GetAwaiter().GetResult();
            var capacityView = new BrowserMainView { DataContext = capacityModel };
            capacityModel.IsSidebarVisible = true;
            host.Content = capacityView;
            Dispatcher.UIThread.RunJobs();
            timer.Stop();
            Check(capacityModel.Bookmarks.Count == 100 && capacityModel.History.Count == 100, "Large catalogs keep first-screen UI collections bounded");
            Console.WriteLine($"Browser UI fixture {count} rows: first-page load/layout {timer.Elapsed.TotalMilliseconds:F2} ms (in-memory transport)");
            capacityView.ClosePlatformBrowser();
        }
        var client = new BrowserFixtureClient();
        var model = new BrowserViewModel(client);
        model.LoadAsync().GetAwaiter().GetResult();
        Check(model.Bookmarks.Count == 100 && model.History.Count == 100, "Initial browser collections are paged");
        model.LoadMoreBookmarksCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(model.Bookmarks.Count == 150 && !model.HasMoreBookmarks, "Second bookmark page is accessible");
        model.LoadMoreHistoryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(model.History.Count == 130 && !model.HasMoreHistory, "Second history page is accessible");

        // Test navigation without a native adapter: tab selection must not issue a new navigation.
        var navigations = 0;
        model.ViewNavigateRequested = _ => navigations++;
        var first = model.SelectedTab!;
        model.NavigateCommand.ExecuteAsync(client.Bookmarks[^1].Url).GetAwaiter().GetResult();
        model.OnNavigationCompleted(first.Source, true);
        Check(model.IsCurrentBookmarked, "Bookmark lookup works outside the first page");
        model.UpdateNavigationState(true, false);
        model.AddTabCommand.Execute(null);
        var second = model.SelectedTab!;
        Check(!model.HasCurrentPage && !model.CanGoBack && model.AddressText == "", "New tab has independent state");
        model.NavigateCommand.ExecuteAsync("http://example.test").GetAwaiter().GetResult();
        model.OnNavigationCompleted(second.Source, true);
        Check(model.ConnectionSymbol != "◈", "HTTP is not presented as HTTPS");
        model.SelectedTab = first;
        Check(navigations == 2 && model.CanGoBack && model.AddressText == first.Source!.ToString(), "Switching restores tab without reloading");
        model.CloseTabCommand.Execute(second);
        Check(model.Tabs.Count == 1 && model.SelectedTab == first, "Closing an inactive tab preserves selection");
        model.CloseSelectedTab();
        Check(model.Tabs.Count == 1 && !model.HasCurrentPage, "Closing the last tab leaves a usable new tab");
        model.ViewNavigateRequested = null;

        foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP" })
        foreach (var theme in new[] { ThemeKind.Light, ThemeKind.Dark })
        foreach (var width in new[] { 1100, 640, 480 })
        {
            settings.Language = culture;
            appearance.Apply(theme, AppearancePreferencesDto.Default, SystemStyleIds.WindowsLike);
            var browser = new BrowserMainView { DataContext = model };
            host.Width = width; host.Height = 620; host.Content = browser;
            Dispatcher.UIThread.RunJobs();
            model.AddTabCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(browser.FindControl<ListBox>("TabStrip")!.SelectedItem, model.SelectedTab), "Tab strip selects the newly created tab");
            model.CloseSelectedTab();
            Dispatcher.UIThread.RunJobs();
            var address = browser.FindControl<TextBox>("AddressBox")!;
            var menu = browser.FindControl<Button>("BrowserMenuButton")!;
            var toolbar = browser.FindControl<Grid>("NavigationToolbar")!;
            Check(address.Bounds.Width > 100, $"Address field remains usable at {width}px");
            var point = menu.TranslatePoint(default, browser)!.Value;
            Check(point.X + menu.Bounds.Width <= browser.Bounds.Width + 1, "Menu fits the viewport");
            Check(toolbar.Bounds.Height <= 40, "Navigation uses a single compact row");
            var flyout = (MenuFlyout)menu.Flyout!;
            flyout.ShowAt(menu);
            Dispatcher.UIThread.RunJobs();
            Check(flyout.Items.OfType<MenuItem>().All(item => item.Command is not null), "Browser menu commands inherit their view model");
            flyout.Hide();
            browser.FocusAddressBox();
            Check(address.IsFocused, "Address shortcut focuses the omnibox");
            Check(!browser.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("[missing:") == true), "Browser labels resolve");
            model.SwitchToBookmarksCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var content = browser.FindControl<Grid>("BrowserContentGrid")!;
            Check(content.ColumnDefinitions[2].ActualWidth >= width * .5, "Sidebar leaves a usable content area");
            model.ToggleSidebarCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Check(content.ColumnDefinitions[0].ActualWidth == 0, "Closing the sidebar returns its width");
            if (culture == "zh-CN" && width != 480)
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, 620));
                bitmap.Render(browser);
                bitmap.Save(Path.Combine(output, $"Browser-{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            browser.ClosePlatformBrowser();
        }
        Console.WriteLine("PASS: Browser pagination, tab state and 18 language/theme/width layouts.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class BrowserFixtureClient : IBrowserClient
{
    private readonly Guid _user = Guid.NewGuid();
    public List<BookmarkDto> Bookmarks { get; }
    public List<HistoryEntryDto> History { get; }
    public BrowserFixtureClient(int bookmarkCount = 150, int historyCount = 130)
    {
        Bookmarks = Enumerable.Range(0, bookmarkCount).Select(i => new BookmarkDto(Guid.NewGuid(), _user, $"Example {i:D3}", $"https://example.test/{i}", DateTimeOffset.UtcNow)).ToList();
        History = Enumerable.Range(0, historyCount).Select(i => new HistoryEntryDto(Guid.NewGuid(), _user, $"Visit {i:D3}", $"https://history.test/{i}", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToList();
    }
    public Task<BrowserSettingsDto> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(BrowserSettingsDto.Default);
    public Task<BrowserSettingsDto> SaveSettingsAsync(BrowserSettingsDto settings, CancellationToken ct = default) => Task.FromResult(settings);
    public Task<IReadOnlyList<BookmarkDto>> ListBookmarksAsync(int offset = 0, int limit = BrowserQueryLimits.DefaultPageSize, string? url = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BookmarkDto>>(Bookmarks.Where(x => url is null || x.Url == url).Skip(offset).Take(limit).ToArray());
    public Task<IReadOnlyList<HistoryEntryDto>> ListHistoryAsync(int offset = 0, int limit = BrowserQueryLimits.DefaultPageSize, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HistoryEntryDto>>(History.Skip(offset).Take(limit).ToArray());
    public Task<BookmarkDto> AddBookmarkAsync(string title, string url, CancellationToken ct = default)
    {
        var item = new BookmarkDto(Guid.NewGuid(), _user, title, url, DateTimeOffset.UtcNow); Bookmarks.Add(item); return Task.FromResult(item);
    }
    public Task<HistoryEntryDto> RecordVisitAsync(string title, string url, CancellationToken ct = default) => Task.FromResult(new HistoryEntryDto(Guid.NewGuid(), _user, title, url, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    public Task DeleteBookmarkAsync(Guid id, CancellationToken ct = default) { Bookmarks.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    public Task DeleteHistoryAsync(Guid id, CancellationToken ct = default) { History.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    public Task ClearBookmarksAsync(CancellationToken ct = default) { Bookmarks.Clear(); return Task.CompletedTask; }
    public Task ClearHistoryAsync(CancellationToken ct = default) { History.Clear(); return Task.CompletedTask; }
}
