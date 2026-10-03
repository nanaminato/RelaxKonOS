using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer.Controls;
using RelaxKonOS.Client.Apps.Explorer.Models;

internal static class FileBrowserPresentationChecks
{
    public static void Run(Action<bool, string> check)
    {
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var entries = new[] { "one", "two", "three" };
        var grid = new DataGrid { ItemsSource = entries, SelectionMode = DataGridSelectionMode.Extended, AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Width = new DataGridLength(180),
            CellTemplate = new FuncDataTemplate<string>((entry, _) => new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    new ExplorerIcon { Width = 22, Height = 22 },
                    new TextBlock { Text = entry, Height = 28, [Grid.ColumnProperty] = 1 },
                },
            }),
        });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Width = new DataGridLength(164),
            CellTemplate = new FuncDataTemplate<string>((_, _) => new TextBlock { Text = "2026/10/03" }),
        });
        var host = new Grid { Background = Avalonia.Media.Brushes.Transparent };
        host.Children.Add(grid);
        var enabled = true;
        var presentation = new FileBrowserPresentation(host, grid, () => enabled);
        var window = new Window { Width = 600, Height = 400, Content = host };
        window.Show();
        Pump();
        var focusCell = grid.GetVisualDescendants().OfType<DataGridCell>().Skip(1).First();
        var cellPoint = focusCell.TranslatePoint(new Point(10, 10), host)!.Value;
        window.MouseDown(cellPoint, MouseButton.Left);
        window.MouseUp(cellPoint, MouseButton.Left);
        Pump();
        check(focusCell.Classes.Contains(":focus"), "Mouse click activates the metadata cell focus state used by the theme");
        check(focusCell.GetVisualDescendants().OfType<Grid>().Single(c => c.Name == "FocusVisual").IsVisible == false,
            "Details never displays a cell focus rectangle");
        var focusRow = focusCell.FindAncestorOfType<DataGridRow>()!;
        check(focusRow.BorderThickness == new Thickness(0), "Details selection does not add a row outline");
        foreach (var mode in Enum.GetValues<ExplorerViewMode>())
        {
            grid.SelectedItem = entries[0];
            presentation.SetMode(mode);
            Pump();
            check(grid.SelectedItems.Contains(entries[0]) && presentation.Items.SelectedItems!.Contains(entries[0]), $"{mode}: switching preserves selection");
            var active = mode == ExplorerViewMode.Details ? (Control)grid : presentation.Items;
            var containers = active.GetVisualDescendants().OfType<Control>().Where(c => c is DataGridRow or ListBoxItem).ToArray();
            check(containers.Length == 3, $"{mode}: all file containers render");
            if (mode == ExplorerViewMode.LargeIcons)
            {
                var labels = containers.Select(c => c.GetVisualDescendants().OfType<TextBlock>().First()).ToArray();
                check(labels.All(label => label.Bounds.Width >= 120), "Large icon labels use the full tile width even for short names");
                check(containers.Select(c => c.Bounds.Height).Distinct().Count() == 1,
                    "Large icon tiles have a uniform height");
                check(containers.All(container =>
                {
                    var icon = container.GetVisualDescendants().OfType<ExplorerIcon>().Single();
                    var label = container.GetVisualDescendants().OfType<TextBlock>().First();
                    var iconOrigin = icon.TranslatePoint(default, container)!.Value;
                    var labelOrigin = label.TranslatePoint(default, container)!.Value;
                    return Math.Abs(iconOrigin.X + icon.Bounds.Width / 2 - labelOrigin.X - label.Bounds.Width / 2) < 0.1;
                }), "Large icons and filenames share the same horizontal center");
            }
            var origin = containers[0].TranslatePoint(default, host)!.Value;
            // Start to the right of the entries and drag back across the first row/item.
            var begin = new Point(560, origin.Y + 2);
            var end = new Point(origin.X + 2, origin.Y + containers[0].Bounds.Height - 2);
            window.MouseDown(begin, MouseButton.Left);
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Pump();
            check(grid.SelectedItems.Contains(entries[0]), $"{mode}: background drag selects intersecting file");
            if (mode == ExplorerViewMode.Details)
                check(grid.GetVisualDescendants().OfType<Grid>().Where(c => c.Name == "FocusVisual" && c.TemplatedParent is DataGridCell).All(c => !c.IsVisible),
                    "Rubber-band selection does not reveal the theme cell focus outline");
            window.MouseDown(new Point(550, 300), MouseButton.Left);
            window.MouseUp(new Point(550, 300), MouseButton.Left);
            check(grid.SelectedItems.Count == 0, $"{mode}: background click clears selection");
        }
        presentation.SetMode(ExplorerViewMode.SmallIcons);
        Pump();
        presentation.Items.SelectedItems!.Add(entries[1]);
        check(grid.SelectedItems.Contains(entries[1]), "Icon click selection reaches command selection");
        grid.SelectAll();
        check(presentation.Items.SelectedItems.Count == 3, "Ctrl+A command selection reaches icon view");
        presentation.SetMode(ExplorerViewMode.LargeIcons);
        Pump();
        grid.SelectedItem = entries[2];
        var first = presentation.Items.GetVisualDescendants().OfType<ListBoxItem>().First();
        var firstPoint = first.TranslatePoint(default, host)!.Value;
        var below = new Point(firstPoint.X + first.Bounds.Width - 2, 280);
        var inside = new Point(firstPoint.X + 2, firstPoint.Y + 2);
        window.MouseDown(below, MouseButton.Left, RawInputModifiers.Control);
        window.MouseMove(inside, RawInputModifiers.Control);
        window.MouseUp(inside, MouseButton.Left, RawInputModifiers.Control);
        check(grid.SelectedItems.Contains(entries[0]) && grid.SelectedItems.Contains(entries[2]) && !grid.SelectedItems.Contains(entries[1]),
            "Ctrl rubber-band adds intersecting icons and preserves prior selection");
        check(grid.SelectedItem is not null, "Icon multi-selection retains command primary entry");
        window.MouseDown(below, MouseButton.Left);
        window.MouseMove(inside);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(inside, MouseButton.Left);
        check(grid.SelectedItems.Contains(entries[0]) && grid.SelectedItems.Contains(entries[2]), "Escape restores pre-drag selection");
        grid.SelectionMode = DataGridSelectionMode.Single;
        window.MouseDown(new Point(560, 280), MouseButton.Left);
        window.MouseMove(inside);
        window.MouseUp(inside, MouseButton.Left);
        check(grid.SelectedItems.Count == 1 && presentation.Items.SelectedItems!.Count == 1, "Picker rubber-band obeys single selection");
        grid.ItemsSource = new[] { "new-folder-file" };
        Pump();
        check(presentation.Items.ItemCount == 1 && grid.SelectedItems.Count == 0, "Directory refresh replaces icon items and clears old selection");
        enabled = false;
        window.MouseDown(new Point(550, 300), MouseButton.Left);
        window.MouseMove(new Point(1, 1));
        window.MouseUp(new Point(1, 1), MouseButton.Left);
        check(grid.SelectedItems.Count == 0, "Busy browser rejects rubber-band selection");
        enabled = true;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        window.Height = 410;
        grid.ItemsSource = Enumerable.Range(0, 80).Select(i => $"file-{i}").ToArray();
        presentation.SetMode(ExplorerViewMode.List);
        Pump();
        var scroll = presentation.Items.GetVisualDescendants().OfType<ScrollViewer>().First();
        var listItems = presentation.Items.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        check(listItems[1].Bounds.X == listItems[0].Bounds.X && listItems[1].Bounds.Y > listItems[0].Bounds.Y
            && listItems.Any(item => item.Bounds.X > listItems[0].Bounds.X && item.Bounds.Y == listItems[0].Bounds.Y),
            "List fills downwards before wrapping into columns");
        check(scroll.Extent.Width > scroll.Viewport.Width && scroll.Extent.Height <= scroll.Viewport.Height,
            "List overflow scrolls horizontally within the viewport height");
        var listBottom = listItems.Where(item => item.Bounds.X == listItems[0].Bounds.X).Max(item => item.TranslatePoint(default, host)!.Value.Y + item.Bounds.Height);
        window.MouseDown(new Point(1, listBottom + 1), MouseButton.Left);
        window.MouseMove(new Point(595, 2));
        using (var cancellation = new CancellationTokenSource(450)) Dispatcher.UIThread.MainLoop(cancellation.Token);
        window.MouseUp(new Point(595, 2), MouseButton.Left);
        check(scroll.Offset.X > 0 && grid.SelectedItems.Count > 10, "List rubber-band scrolls horizontally and selects newly exposed entries");
        window.Close();
        SynchronizationContext.SetSynchronizationContext(null);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://RelaxKonOS.Explorer.Tests/"))
            {
                Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
            });
        }
    }
}
