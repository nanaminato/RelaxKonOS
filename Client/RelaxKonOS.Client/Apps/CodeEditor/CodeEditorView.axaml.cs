using Avalonia;
using System.ComponentModel;
using Avalonia.Controls;
using AvaloniaEdit.Highlighting;

namespace RelaxKonOS.Client.Apps.CodeEditor;

public partial class CodeEditorView : UserControl
{
    private const double DefaultSidebarWidth = 250;
    private const double SidebarSplitterWidth = 6;

    private CodeEditorViewModel? _viewModel;
    private double _sidebarWidth = DefaultSidebarWidth;

    private ColumnDefinition SidebarColumn => EditorContentGrid.ColumnDefinitions[1];
    private ColumnDefinition SidebarSplitterColumn => EditorContentGrid.ColumnDefinitions[2];

    public CodeEditorView()
    {
        InitializeComponent();
        Editor.Document.TextChanged += OnEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as CodeEditorViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Editor.Text = _viewModel.Text;
            UpdateSyntaxHighlighting(_viewModel.CurrentPath);
            UpdateSidebarLayout(_viewModel.IsSidebarVisible);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(CodeEditorViewModel.Text) && Editor.Text != _viewModel?.Text)
            Editor.Text = _viewModel?.Text ?? string.Empty;
        else if (eventArgs.PropertyName == nameof(CodeEditorViewModel.CurrentPath))
            UpdateSyntaxHighlighting(_viewModel?.CurrentPath);
        else if (eventArgs.PropertyName == nameof(CodeEditorViewModel.IsSidebarVisible) && _viewModel is not null)
            UpdateSidebarLayout(_viewModel.IsSidebarVisible);
    }

    private void OnEditorTextChanged(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null && _viewModel.Text != Editor.Text)
            _viewModel.Text = Editor.Text;
    }

    private void UpdateSyntaxHighlighting(string? path)
    {
        Editor.SyntaxHighlighting = string.IsNullOrWhiteSpace(path)
            ? null
            : HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
    }

    /// <summary>Collapsing the sidebar releases its space while retaining the user's dragged width.</summary>
    private void UpdateSidebarLayout(bool isVisible)
    {
        if (isVisible)
        {
            SidebarColumn.Width = new GridLength(_sidebarWidth, GridUnitType.Pixel);
            SidebarSplitterColumn.Width = new GridLength(SidebarSplitterWidth, GridUnitType.Pixel);
            return;
        }

        if (SidebarColumn.ActualWidth > 0)
            _sidebarWidth = SidebarColumn.ActualWidth;

        SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
        SidebarSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
    }
}
