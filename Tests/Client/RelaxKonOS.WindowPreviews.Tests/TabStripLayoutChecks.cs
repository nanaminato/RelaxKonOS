using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Client.Apps.Browser.ViewModels;
using RelaxKonOS.Client.Apps.Browser.Views;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;

/// <summary>
/// Both title-bar strips answer a crowded row the way their own platform does: the browser narrows
/// every tab until only its own mark is left, the file browser narrows down to a floor and then
/// scrolls. Neither ever moves the + away from the last tab or hides a tab out of reach.
/// </summary>
internal static class TabStripLayoutChecks
{
    public static void Run()
    {
        BrowserStrip();
        ExplorerStrip();
        Console.WriteLine("Tab strip layout checks passed (38 scenarios).");
    }

    private static void BrowserStrip()
    {
        var model = new BrowserViewModel(DispatchProxy.Create<IBrowserClient, HeaderPreviewPermissions>());
        var view = new BrowserMainView { DataContext = model };
        var host = new Window { Width = 900, Height = 420, Content = view };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var header = view.FindControl<Grid>("HeaderTabs")!;
        var scroller = view.FindControl<ScrollViewer>("TabScroller")!;
        var strip = view.FindControl<ListBox>("TabStrip")!;
        var picker = view.FindControl<Button>("TabPickerButton")!;
        var add = view.FindControl<Button>("AddTabButton")!;

        Check(ReferenceEquals(header.Children[0], picker), "The browser's tab list leads its strip");
        Check(add.Parent is StackPanel { } row && ReferenceEquals(row.Parent, scroller) && row.Children.Count == 2
            && ReferenceEquals(row.Children[0], strip) && ReferenceEquals(row.Children[1], add),
            "The browser's new-tab command follows the last tab inside the strip's own scroll row");
        var pickerFlyout = (Flyout)picker.Flyout!;
        pickerFlyout.ShowAt(picker);
        Dispatcher.UIThread.RunJobs();
        Check(pickerFlyout.Content is ListBox { } list && ReferenceEquals(list.ItemsSource, model.Tabs)
            && list.ItemCount == model.Tabs.Count,
            "The browser's tab list enumerates the same tabs as the strip");
        pickerFlyout.Hide();
        Dispatcher.UIThread.RunJobs();

        // Production moves the header out of the UserControl into window chrome.
        // Verify the strip keeps its own styles after that same ownership change.
        header.DataContext = model;
        view.FindControl<DockPanel>("ContentDock")!.Children.Remove(header);
        host.Content = null;
        host.Content = new DockPanel { Children = { header, view } };
        Dispatcher.UIThread.RunJobs();
        var wideTab = (Control)strip.ContainerFromIndex(0)!;
        Check(CloseMark(wideTab) == 1, "A reparented wide browser tab keeps its close button");
        var wideMark = wideTab.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("tab-mark"));
        Check(wideMark.IsVisible, "A wide browser tab keeps its site icon beside its close button");

        var lone = view.TabContentWidth;
        Check(view.IsTabTitleVisible && lone >= 200, $"A lone browser tab keeps the widest share ({lone})");

        for (var index = 0; index < 12; index++) model.AddTabCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var crowded = view.TabContentWidth;
        Check(model.Tabs.Count == 13 && crowded < lone, $"Every extra browser tab narrows the others ({lone} -> {crowded})");
        Check(!view.IsTabTitleVisible, "A crowded browser strip drops the titles but keeps every mark");
        var firstTab = strip.ContainerFromIndex(0);
        var firstPitch = firstTab is { } measured ? measured.Bounds.Width + measured.Margin.Left + measured.Margin.Right : 0;
        Check(firstTab is not null && Math.Abs(firstPitch - (view.TabContentWidth + 18)) < 0.6,
            $"A browser tab takes the share it was measured for (tab={firstTab?.Bounds.Width}, share={view.TabContentWidth},"
            + $" strip={strip.Bounds.Width}, title={view.TabTitleWidth}, viewport={scroller.Viewport.Width}, extent={scroller.Extent.Width})");
        Check(scroller.Extent.Width <= scroller.Viewport.Width + 1, "Thirteen tabs and the + still fit the row they divided");

        for (var index = 0; index < 20; index++) model.AddTabCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Check(view.TabContentWidth == 18, "A browser tab never narrows past its own mark");
        Check(scroller.Extent.Width > scroller.Viewport.Width, "Past its floor the browser strip scrolls instead of shrinking further");
        Check(scroller.Offset.X > 0, "The active browser tab stays in view after the strip grows");
        Check(add.TranslatePoint(default, scroller) is { } addOrigin
            && addOrigin.X + add.Bounds.Width <= scroller.Viewport.Width + 1,
            "Standing on the last browser tab still leaves the + inside the strip's window");

        var atEnd = scroller.Offset.X;
        strip.SelectedItem = model.Tabs[0];
        Dispatcher.UIThread.RunJobs();
        Check(scroller.Offset.X < atEnd && TabIsInView(strip, scroller, model.Tabs[0]),
            "Choosing a browser tab scrolls the strip back to it");
        Check(view.ScrollTabsBy(-1) && scroller.Offset.X > 0, "The wheel scrolls a crowded browser strip");
        Check(view.ScrollTabsBy(1) && scroller.Offset.X == 0, "The wheel scrolls the browser strip back");

        var active = strip.ContainerFromItem(strip.SelectedItem!) as Control;
        var background = strip.ContainerFromItem(model.Tabs[1]) as Control;
        Check(active is not null && background is not null, "Both browser tabs have containers");
        Check(CloseMark(active!) >= 0.9 && CloseMark(background!) <= 0.1,
            "Only the browser tab the user is on states its close mark");
        // The location mark and the close mark share the tab's one slot, so the location mark has to
        // stand down while the close mark is up rather than being drawn through by it.
        static double MarkOpacity(Control tab) => tab.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("tab-mark")).IsVisible ? 1 : 0;
        Check(MarkOpacity(active!) <= 0.1 && MarkOpacity(background!) > 0.1,
            "Only the browser tab the user is on hands its slot to the close mark");

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ExplorerStrip()
    {
        var client = DispatchProxy.Create<IExplorerClient, HeaderFiles>();
        using var explorer = new ExplorerViewModel(client);
        explorer.NavigateToAsync("/a").GetAwaiter().GetResult();
        var view = new ExplorerMainView { DataContext = explorer };
        var host = new Window { Width = 700, Height = 420, Content = view };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var header = view.FindControl<Grid>("HeaderTabs")!;
        var scroller = view.FindControl<ScrollViewer>("TabScroller")!;
        var left = view.FindControl<Button>("TabScrollLeft")!;
        var right = view.FindControl<Button>("TabScrollRight")!;
        var add = view.FindControl<Button>("AddTabButton")!;

        Check(ReferenceEquals(header.Children[0], left) && ReferenceEquals(header.Children[2], right)
            && add.Parent is StackPanel row && ReferenceEquals(row.Parent, scroller)
            && ReferenceEquals(row.Children[^1], add),
            "The file browser's + follows the last tab inside the scrolling row");
        Check(!left.IsVisible && !right.IsVisible, "A short folder strip states no scroll marks");

        var folders = view.FindControl<ListBox>("FolderTabs")!;
        Check(add.TranslatePoint(default, scroller) is { } plusOrigin
            && Math.Abs(plusOrigin.X - folders.Bounds.Width - 4) < 1,
            "The folder + sits immediately after a short tab strip");

        var lone = view.TabContentWidth;
        for (var index = 0; index < 3; index++)
        {
            explorer.OpenTabAsync($"/t{index}").GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
        }
        Check(view.TabContentWidth < lone, $"Every extra folder tab narrows the others ({lone} -> {view.TabContentWidth})");

        for (var index = 0; index < 9; index++)
        {
            explorer.OpenTabAsync($"/u{index}").GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
        }
        Check(explorer.Tabs.Count >= 12, $"The file browser opened its tabs ({explorer.Tabs.Count})");
        Check(view.TabContentWidth == 72, "A folder tab stops at its floor instead of turning into a sliver");
        Check(left.IsVisible && right.IsVisible, "A filled folder strip states its scroll marks");
        Check(scroller.Extent.Width > scroller.Viewport.Width, "A filled folder strip scrolls instead of hiding a folder");
        Check(add.TranslatePoint(default, scroller) is { } crowdedPlus
            && crowdedPlus.X + add.Bounds.Width <= scroller.Viewport.Width + 1,
            "Selecting the last folder keeps its trailing + visible");

        host.Width = 1600;
        Dispatcher.UIThread.RunJobs();
        Check(!left.IsVisible && !right.IsVisible, "Widening a crowded folder strip removes its scroll arrows");
        host.Width = 700;
        Dispatcher.UIThread.RunJobs();
        Check(left.IsVisible && right.IsVisible, "Narrowing the folder strip restores overflow navigation");

        explorer.SwitchTabAsync(explorer.Tabs[0]).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Check(scroller.Offset.X == 0 && !left.IsEnabled && right.IsEnabled,
            "The first folder leaves the strip at its start, with only the right mark awake");

        right.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(scroller.Offset.X > 0 && left.IsEnabled, "The right mark moves the strip and wakes its neighbour");
        left.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(scroller.Offset.X == 0, "The left mark returns the strip to its first tab");
        Check(view.ScrollTabsBy(1) && scroller.Offset.X > 0, "The wheel scrolls a filled folder strip");
        Check(view.ScrollTabsBy(-1) && scroller.Offset.X == 0, "The wheel scrolls the folder strip back");

        while (explorer.Tabs.Count > 2)
        {
            explorer.CloseTabCommand.ExecuteAsync(explorer.Tabs[^1]).GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
        }
        Check(!left.IsVisible && !right.IsVisible && scroller.Offset.X == 0,
            "Closing back down to a short strip takes the scroll marks away");

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool TabIsInView(ListBox strip, ScrollViewer scroller, object item)
    {
        if (strip.ContainerFromItem(item) is not Control container
            || container.TranslatePoint(default, scroller) is not { } origin) return false;
        return origin.X >= -1 && origin.X + container.Bounds.Width <= scroller.Viewport.Width + 1;
    }

    private static double CloseMark(Control tab) =>
        tab.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("tab-close")).IsVisible ? 1 : 0;

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
