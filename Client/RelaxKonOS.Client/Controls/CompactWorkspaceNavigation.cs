using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Controls;

/// <summary>Preserves workspace page access when a sidebar would consume the narrow viewport.</summary>
internal static class CompactWorkspaceNavigation
{
    public static void Install(UserControl view, Grid workspace)
    {
        var sidebar = workspace.Children.OfType<Control>().First(control => Grid.GetColumn(control) == 0);
        var content = workspace.Children.OfType<Control>().Where(control => Grid.GetColumn(control) == 1).ToArray();
        var columns = workspace.ColumnDefinitions;
        var rows = workspace.RowDefinitions;
        var menu = new MenuFlyout();
        var toggle = new Button { Name = "CompactWorkspaceMenu", Content = "☰", IsVisible = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(8, 4), MinWidth = 32 };
        toggle.Classes.Add("header-action");
        toggle.Bind(ToolTip.TipProperty, (Binding)new LocExtension("common.view").ProvideValue(null!));
        toggle.Bind(AutomationProperties.NameProperty, (Binding)new LocExtension("common.view").ProvideValue(null!));
        toggle.Flyout = menu;
        // Populate after the sidebar templates exist; labels keep their original localization binding.
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var source in sidebar.GetLogicalDescendants().OfType<Button>().Where(button => button.Tag is string))
            {
                var item = new MenuItem();
                if (source.Content is string) item.Bind(MenuItem.HeaderProperty, new Binding(nameof(Button.Content)) { Source = source });
                else if (source.Content is Control visualContent
                    && (visualContent as TextBlock ?? visualContent.GetLogicalDescendants().OfType<TextBlock>().LastOrDefault()) is { } label)
                    item.Bind(MenuItem.HeaderProperty, new Binding(nameof(TextBlock.Text)) { Source = label });
                else continue;
                item.Bind(MenuItem.IsEnabledProperty, new Binding(nameof(Button.IsEffectivelyEnabled)) { Source = source });
                item.Bind(MenuItem.IsVisibleProperty, new Binding(nameof(Button.IsVisible)) { Source = source });
                item.Click += (_, _) => { source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); menu.Hide(); };
                menu.Items.Add(item);
            }
        };
        workspace.Children.Add(toggle);
        var compact = false;
        view.SizeChanged += (_, e) =>
        {
            var next = e.NewSize.Width < 620;
            if (next == compact) return;
            compact = next; menu.Hide();
            sidebar.IsVisible = !compact; toggle.IsVisible = compact;
            workspace.ColumnDefinitions = compact ? new ColumnDefinitions("*") : columns;
            workspace.RowDefinitions = compact ? new RowDefinitions("Auto,*") : rows;
            foreach (var control in content) { Grid.SetColumn(control, compact ? 0 : 1); Grid.SetRow(control, compact ? 1 : 0); }
        };
    }
}
