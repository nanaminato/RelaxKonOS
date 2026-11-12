using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using RelaxKonOS.Client.Apps.Explorer.Controls;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

/// <summary>Responsive This PC overview. Capacity is never inferred from file listings.</summary>
internal sealed class ComputerOverviewView : ScrollViewer
{
    public event Action<ComputerDriveCard>? OpenRequested;
    public ComputerOverviewView() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled; }

    public void Update(IEnumerable<FileSystemEntryDto> drives, IEnumerable<CloudConnectionDto> accounts)
    {
        var sections = new StackPanel { Margin = new Thickness(20), Spacing = 18 };
        AddSection(sections, "explorer.devices_drives", drives.Where(d => d.Type == FileSystemEntryType.Drive).Select(ComputerDriveCard.Local).ToArray());
        AddSection(sections, "explorer.cloud.title", accounts.Select(ComputerDriveCard.Cloud).ToArray());
        Content = sections;
    }

    private void AddSection(StackPanel parent, string title, IReadOnlyList<ComputerDriveCard> cards)
    {
        if (cards.Count == 0) return;
        parent.Children.Add(new TextBlock { Text = LocalizedText.Get(title), FontSize = 17, FontWeight = FontWeight.SemiBold });
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var card in cards)
        {
            var text = new StackPanel { Spacing = 5 };
            text.Children.Add(new TextBlock { Text = card.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = card.Detail, FontSize = 11, Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis });
            if (card.UsedPercent is { } used)
                text.Children.Add(new ProgressBar { Value = used, Minimum = 0, Maximum = 100, Height = 5 });
            text.Children.Add(new TextBlock { Text = card.Capacity, FontSize = 12, Opacity = .8, TextTrimming = TextTrimming.CharacterEllipsis });
            var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*"), ColumnSpacing = 12 };
            layout.Children.Add(new ExplorerIcon { TreeIconKind = TreeNodeIconKind.Drive, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(text, 1);
            layout.Children.Add(text);
            var button = new Button { Content = layout, Width = 290, MinHeight = 112, Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(button, card.ConnectionId is { } id ? $"{card.Name}\n{card.Detail}\n{id}" : card.Path);
            button.Click += (_, _) => OpenRequested?.Invoke(card);
            wrap.Children.Add(button);
        }
        parent.Children.Add(wrap);
    }
}
