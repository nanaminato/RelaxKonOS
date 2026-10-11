using Avalonia;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.Search;
using RelaxKonOS.WindowManager;
using AvaloniaEdit.Highlighting;

namespace RelaxKonOS.Client.Apps.CodeEditor;

public partial class CodeEditorView : UserControl
{
    private const double DefaultSidebarWidth = 250;
    private const double SidebarSplitterWidth = 6;

    private CodeEditorViewModel? _viewModel;
    private readonly SearchPanel _searchPanel;
    private double _sidebarWidth = DefaultSidebarWidth;

    private ColumnDefinition SidebarColumn => EditorContentGrid.ColumnDefinitions[1];
    private ColumnDefinition SidebarSplitterColumn => EditorContentGrid.ColumnDefinitions[2];

    public CodeEditorView()
    {
        InitializeComponent();
        _searchPanel = SearchPanel.Install(Editor);
        // Keep the interactive overlay above all text-area layers, including the caret.
        // Otherwise those layers can intercept clicks on the search and replace controls.
        _searchPanel.ZIndex = int.MaxValue;
        SizeChanged += (_, e) =>
        {
            HeaderMenu.IsVisible = e.NewSize.Width >= 600;
            CompactMenu.IsVisible = !HeaderMenu.IsVisible;
            SidebarColumn.MaxWidth = Math.Max(160, e.NewSize.Width - 320);
            if (_viewModel?.IsSidebarVisible == true && (e.NewSize.Width < 600) != (SidebarColumn.Width.Value == 0))
                UpdateSidebarLayout(true);
        };
        DocumentTabs.SizeChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (DocumentTabs.SelectedItem is { } document) DocumentTabs.ScrollIntoView(document);
        }, DispatcherPriority.Loaded);
        DocumentTabs.SelectionChanged += (_, _) =>
        {
            if (DocumentTabs.SelectedItem is { } document) DocumentTabs.ScrollIntoView(document);
        };
        Editor.Document.TextChanged += OnEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
    }

    public void AttachWindowHeader(ManagedWindow window)
    {
        foreach (var block in new Control[] { HeaderLeading, HeaderCenter, HeaderTrailing, HeaderTabs })
            block.DataContext = DataContext;
        HeaderHost.Children.Clear();
        window.View.HeaderLeading = HeaderLeading;
        window.View.HeaderCenter = HeaderCenter;
        window.View.HeaderTrailing = HeaderTrailing;
        window.View.HeaderTabs = HeaderTabs;
    }

    public void FocusFind()
    {
        if (_viewModel?.IsTextDocument != true) return;
        _searchPanel.Open();
        Dispatcher.UIThread.Post(() =>
        {
            if (_searchPanel.IsOpened) _searchPanel.Reactivate();
        }, DispatcherPriority.Loaded);
    }

    private void DocumentPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => DocumentPickerButton.Flyout?.Hide();

    public void CycleDocument(int direction)
    {
        if (_viewModel?.ActiveDocument is not { } active || _viewModel.OpenDocuments.Count == 0) return;
        var documents = _viewModel.OpenDocuments;
        _viewModel.ActiveDocument = documents[(documents.IndexOf(active) + direction + documents.Count) % documents.Count];
    }

    private void Find_Click(object? sender, RoutedEventArgs e) => FocusFind();

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as CodeEditorViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Editor.Text = _viewModel.Text;
            Editor.IsReadOnly = _viewModel.IsSaving || !_viewModel.IsTextDocument;
            UpdateSyntaxHighlighting(_viewModel.CurrentPath);
            UpdateSidebarLayout(_viewModel.IsSidebarVisible);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(CodeEditorViewModel.Text) && Editor.Text != _viewModel?.Text)
            Editor.Text = _viewModel?.Text ?? string.Empty;
        else if ((eventArgs.PropertyName == nameof(CodeEditorViewModel.IsSaving)
            || eventArgs.PropertyName == nameof(CodeEditorViewModel.IsTextDocument)) && _viewModel is not null)
        {
            Editor.IsReadOnly = _viewModel.IsSaving || !_viewModel.IsTextDocument;
            if (!_viewModel.IsTextDocument) _searchPanel.Close();
        }
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
        if (isVisible && (Bounds.Width <= 0 || Bounds.Width >= 600))
        {
            SidebarColumn.Width = new GridLength(_sidebarWidth, GridUnitType.Pixel);
            SidebarSplitterColumn.Width = new GridLength(SidebarSplitterWidth, GridUnitType.Pixel);
            return;
        }

        if (SidebarColumn.Width.Value > 0)
            _sidebarWidth = SidebarColumn.Width.Value;

        SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
        SidebarSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
    }
}
