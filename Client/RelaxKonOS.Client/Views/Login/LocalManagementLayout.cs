using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Automation;
using RelaxKonOS.Client.Services.Theming;

namespace RelaxKonOS.Client.Views.Login;

internal static class LocalManagementLayout
{
    public static TextBlock Text(string text, double size = 14, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrushes.Get(muted ? "TextSecondaryBrush" : "TextPrimaryBrush")
    };

    public static Image Icon(string name, double size = 28) => new()
    {
        Source = new Bitmap(AssetLoader.Open(new Uri($"avares://RelaxKonOS.Client/Assets/Icons/Fluent/{name}.png"))),
        Width = size, Height = size, Stretch = Stretch.Uniform
    };

    public static Border Card(Control child) => new()
    {
        Child = child, Padding = new Thickness(18), CornerRadius = new CornerRadius(12),
        Background = ThemeBrushes.Get("SurfaceBrush"), BorderBrush = ThemeBrushes.Get("BorderSubtleBrush"), BorderThickness = new Thickness(1)
    };

    public static Control Header(string title, string subtitle, string icon)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(Icon(icon, 36));
        var text = new StackPanel { Spacing = 5, Margin = new Thickness(16, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Text(subtitle, 13, true));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    public static Button Action(string title, string description, string icon, Action action, bool enabled = true)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        content.Children.Add(Icon(icon));
        var text = new StackPanel { Spacing = 5, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Text(description, 12, true));
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        var button = new Button { Content = content, IsEnabled = enabled, Padding = new Thickness(16), Margin = new Thickness(5),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = ThemeBrushes.Get("SurfaceBrush"), BorderBrush = ThemeBrushes.Get("BorderSubtleBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), MinHeight = 90 };
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, description);
        return button;
    }

    public static Grid Shell(Control header, Control body, Control? footer = null)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Background = ThemeBrushes.Get("AppBackgroundBrush") };
        grid.Children.Add(new Border { Child = header, Padding = new Thickness(24, 22) });
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(20, 0, 20, 16) };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        if (footer is not null)
        {
            var border = new Border { Child = footer, Padding = new Thickness(24, 14), Background = ThemeBrushes.Get("SurfaceBrush"), BorderBrush = ThemeBrushes.Get("BorderSubtleBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(border, 2);
            grid.Children.Add(border);
        }
        return grid;
    }
}
