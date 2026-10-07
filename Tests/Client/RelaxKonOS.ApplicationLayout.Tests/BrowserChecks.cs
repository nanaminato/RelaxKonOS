using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
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
        VerifyAddressInput();
        VerifyHistoryGrouping();
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
            foreach (var button in browser.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("browser-action")))
                Check(button.HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Center
                    && button.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Center, "All browser action icons are centered");
            foreach (var name in new[] { "BrowserMenuButton", "SidebarButton" })
            {
                var button = browser.FindControl<Button>(name)!;
                var icon = button.GetVisualDescendants().OfType<PathIcon>().First(icon => icon.IsEffectivelyVisible);
                var offset = icon.TranslatePoint(default, button)!.Value;
                Check(Math.Abs(offset.X + icon.Bounds.Width / 2 - button.Bounds.Width / 2) < 1
                    && Math.Abs(offset.Y + icon.Bounds.Height / 2 - button.Bounds.Height / 2) < 1, "More and collection icons are geometrically centered");
            }
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
            Check(content.ColumnDefinitions[0].ActualWidth >= width * .5, "Sidebar leaves a usable content area");
            if (culture == "zh-CN" && width != 480)
            {
                Capture(browser, width, output, $"Browser-Bookmarks-{theme}-{width}.png");
                model.SwitchToHistoryCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Check(browser.FindControl<ListBox>("HistoryList")!.IsEffectivelyVisible, "History selection displays the grouped list");
                Capture(browser, width, output, $"Browser-History-{theme}-{width}.png");
            }
            if (culture == "zh-CN" && theme == ThemeKind.Light && width == 1100)
            {
                model.SwitchToBookmarksCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                VerifyEntryActions(browser, model, client);
            }
            model.ToggleSidebarCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Check(content.ColumnDefinitions[2].ActualWidth == 0, "Closing the sidebar returns its width");
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

    private static void Capture(BrowserMainView browser, int width, string output, string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, 620));
        bitmap.Render(browser);
        bitmap.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
    }

    private static void VerifyEntryActions(BrowserMainView browser, BrowserViewModel model, BrowserFixtureClient client)
    {
        var more = browser.FindControl<ListBox>("BookmarksList")!.GetVisualDescendants().OfType<Button>().First(button => button.Flyout is MenuFlyout);
        var popup = (MenuFlyout)more.Flyout!;
        popup.ShowAt(more); Dispatcher.UIThread.RunJobs();
        var items = popup.Items.OfType<MenuItem>().ToArray();
        Check(items.All(item => item.DataContext is BookmarkDto), "Row menus inherit their bookmark rather than the browser VM");
        var bookmark = (BookmarkDto)items[0].DataContext!;
        Uri? navigated = null;
        model.ViewNavigateRequested = uri => navigated = uri;
        var tabs = model.Tabs.Count;
        items.Single(item => item.Tag as string == "new-tab").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(model.Tabs.Count == tabs + 1 && navigated?.ToString() == bookmark.Url, "Row action opens the bookmark in a new tab");
        model.CloseSelectedTab();
        items.Single(item => item.Tag as string == "copy").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(TopLevel.GetTopLevel(browser)!.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult() == bookmark.Url, "Row action copies the complete URL");
        items.Single(item => item.Tag as string == "delete").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(!client.Bookmarks.Any(item => item.Id == bookmark.Id), "Row action deletes the selected bookmark");
        popup.Hide();
        model.SwitchToHistoryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        more = browser.FindControl<ListBox>("HistoryList")!.GetVisualDescendants().OfType<Button>().First(button => button.Flyout is MenuFlyout);
        popup = (MenuFlyout)more.Flyout!;
        popup.ShowAt(more); Dispatcher.UIThread.RunJobs();
        items = popup.Items.OfType<MenuItem>().ToArray();
        Check(items.All(item => item.DataContext is BrowserHistoryRow), "History row menus inherit their presentation record");
        var history = ((BrowserHistoryRow)items[0].DataContext!).Item;
        tabs = model.Tabs.Count;
        items.Single(item => item.Tag as string == "new-tab").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(model.Tabs.Count == tabs + 1 && navigated?.ToString() == history.Url, "History opens in a new tab");
        model.CloseSelectedTab();
        items.Single(item => item.Tag as string == "copy").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(TopLevel.GetTopLevel(browser)!.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult() == history.Url, "History copies its complete URL");
        items.Single(item => item.Tag as string == "delete").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(!client.History.Any(item => item.Id == history.Id) && !model.HistoryRows.Any(row => row.Item.Id == history.Id), "Deleting a visit updates the date-grouped list");
        popup.Hide();
        model.ViewNavigateRequested = null;
    }

    private static void VerifyHistoryGrouping()
    {
        var model = new BrowserViewModel(new BrowserFixtureClient());
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 6; i++)
            model.History.Add(new HistoryEntryDto(Guid.NewGuid(), Guid.NewGuid(), $"Day {i}", $"https://day.test/{i}", 1, now, now.AddDays(-(i / 2))));
        Check(model.HistoryRows.Select(row => row.ShowDateHeading).SequenceEqual(new[] { true, false, true, false, true, false }), "History dates have one heading per contiguous day");
        model.History.RemoveAt(0);
        Check(model.HistoryRows[0].ShowDateHeading && model.HistoryRows[1].ShowDateHeading, "Deleting the first visit preserves date boundaries");
        model.History[1] = model.History[1] with { LastVisitedAt = now };
        Check(!model.HistoryRows[1].ShowDateHeading && model.HistoryRows[2].ShowDateHeading, "Replacing a visit updates its following date boundary");
        model.History.Clear();
        Check(model.HistoryRows.Count == 0 && !model.HasHistory, "Clearing history resets the grouped list and empty state");
        model.SwitchToHistoryCommand.Execute(null);
        Check(model.IsHistoryEmpty && !model.IsBookmarksEmpty, "Only the current category's empty state is shown");
        model.SwitchToBookmarksCommand.Execute(null);
        Check(model.IsBookmarksEmpty && !model.IsHistoryEmpty, "Empty state follows the category selector");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void VerifyAddressInput()
    {
        var model = new BrowserViewModel(new BrowserFixtureClient());
        Uri? navigated = null;
        model.ViewNavigateRequested = uri => navigated = uri;
        foreach (var (input, expected) in new[]
        {
            (" https://192.168.1.9:5000/ ", "https://192.168.1.9:5000/"),
            ("example.test/path", "https://example.test/path"),
            ("localhost:5000", "http://localhost:5000/"),
            ("127.0.0.1:5000", "http://127.0.0.1:5000/"),
            ("[::1]:5000", "http://[::1]:5000/")
        })
        {
            navigated = null;
            model.NavigateCommand.ExecuteAsync(input).GetAwaiter().GetResult();
            Check(navigated?.AbsoluteUri == expected, $"Address normalizes correctly: {input}");
        }
        foreach (var input in new[] { "search words", "read.example\tmore", "read.example\nmore", "中文搜索" })
        {
            navigated = null;
            model.NavigateCommand.ExecuteAsync(input).GetAwaiter().GetResult();
            Check(navigated?.Host == "www.bing.com" && Uri.UnescapeDataString(navigated.Query) == "?q=" + input,
                "Search text including whitespace is encoded without changing the query");
        }
        var original = model.SelectedTab!.Source;
        foreach (var input in new string?[]
        {
            null, "", " \t\n ", "https://", "https://[broken.example/", "[broken.example",
            "https://example.test:invalid", "example.test:invalid", "localhost:70000", "https://example.test:70000"
        })
        {
            navigated = null;
            model.NavigateCommand.ExecuteAsync(input).GetAwaiter().GetResult();
            Check(navigated is null && model.SelectedTab.Source == original, $"Invalid address is rejected without throwing or replacing the current tab: {input}");
            Check(model.StatusText.ToString() == RelaxKonOS.Client.Localization.LocalizedText.Get("browser.status.invalid_address"), "Invalid address reports the localized status");
        }
        Console.WriteLine("PASS: Browser malformed-address regression and URL/search normalization.");
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
        History = Enumerable.Range(0, historyCount).Select(i => new HistoryEntryDto(Guid.NewGuid(), _user, $"Visit {i:D3}", $"https://history.test/{i}", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-(i / 3)))).ToList();
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
