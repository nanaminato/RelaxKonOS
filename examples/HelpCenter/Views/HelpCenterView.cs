using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.UI.Themes;
using RelaxKonOS.Examples.HelpCenter.Controls;
using RelaxKonOS.Examples.HelpCenter.Services;

namespace RelaxKonOS.Examples.HelpCenter.Views;

/// <summary>Two-pane help UI: language-aware tree on the left and a read-only Markdown renderer on the right.</summary>
public sealed class HelpCenterView : UserControl
{
    private readonly HelpCenterViewModel _viewModel;
    private readonly TreeView _tree;
    private readonly MarkdownDocumentView _document;
    private readonly TextBlock _status;

    public HelpCenterView(HelpCenterViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        var languagePicker = new ComboBox
        {
            ItemsSource = viewModel.Languages,
            SelectedItem = viewModel.SelectedLanguage,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<HelpLanguage>((language, _) => new TextBlock { Text = language.DisplayName }),
        };
        languagePicker.SelectionChanged += (_, _) =>
        {
            if (languagePicker.SelectedItem is HelpLanguage language)
                _viewModel.SelectLanguage(language.Code);
        };

        _tree = new TreeView
        {
            ItemsSource = viewModel.Tree,
            Margin = new Thickness(0, 14, 0, 0),
            ItemTemplate = new FuncTreeDataTemplate<HelpTreeNode>((node, _) => new TextBlock
            {
                Text = node.Title,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4),
            }, node => node.Children),
        };
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedItem is HelpTreeNode { Document: { } document })
                _viewModel.Open(document);
        };

        var left = new DockPanel { Width = 278, Margin = new Thickness(18), LastChildFill = true };
        DockPanel.SetDock(languagePicker, Dock.Top);
        left.Children.Add(languagePicker);
        left.Children.Add(_tree);

        _document = new MarkdownDocumentView { Margin = new Thickness(30, 24) };
        _status = new TextBlock { Foreground = ThemeResources.Brush("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 0, 30, 16) };
        var right = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        right.Children.Add(new ScrollViewer { Content = _document, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("315,*") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = grid;

        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(HelpCenterViewModel.Tree) or nameof(HelpCenterViewModel.SelectedLanguage))
            {
                _tree.ItemsSource = viewModel.Tree;
                languagePicker.SelectedItem = viewModel.SelectedLanguage;
            }
            if (change.PropertyName == nameof(HelpCenterViewModel.CurrentDocument))
                _document.SetDocument(viewModel.CurrentDocument);
            if (change.PropertyName == nameof(HelpCenterViewModel.Status))
                _status.Text = viewModel.Status;
        };
        _document.SetDocument(viewModel.CurrentDocument);
    }
}
