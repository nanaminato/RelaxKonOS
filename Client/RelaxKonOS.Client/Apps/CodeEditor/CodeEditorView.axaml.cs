using Avalonia;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.Search;
using RelaxKonOS.WindowManager;
using AvaloniaEdit.Highlighting;
using Avalonia.Input;

namespace RelaxKonOS.Client.Apps.CodeEditor;

public partial class CodeEditorView : UserControl
{
    private const double DefaultSidebarWidth = 250;
    private const double SidebarSplitterWidth = 6;

    private CodeEditorViewModel? _viewModel;
    private readonly SearchPanel _searchPanel;
    private double _sidebarWidth = DefaultSidebarWidth;
    private bool _projectLayout;
    private CodeEditorDocument? _displayedDocument;

    private ColumnDefinition SidebarColumn => EditorContentGrid.ColumnDefinitions[1];
    private ColumnDefinition SidebarSplitterColumn => EditorContentGrid.ColumnDefinitions[2];
    public Control ProjectToolContent => FolderPanel;

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
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.CaretPosition = $"{Editor.TextArea.Caret.Line}:{Editor.TextArea.Caret.Column}";
        };
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

    /// <summary>Reuse the editor/session without an application menu or nested activity bar.</summary>
    public void UseProjectLayout()
    {
        _projectLayout = true;
        HeaderHost.Children.Clear();
        HeaderHost.ColumnDefinitions = new ColumnDefinitions("*");
        HeaderHost.RowDefinitions = new RowDefinitions("Auto");
        Grid.SetRow(HeaderTabs, 0);
        Grid.SetColumn(HeaderTabs, 0);
        Grid.SetColumnSpan(HeaderTabs, 1);
        HeaderHost.Children.Add(HeaderTabs);
        ActivityBar.IsVisible = false;
        FolderActions.IsVisible = false;
        FolderToolbar.IsVisible = false;
        FolderSplitter.IsVisible = false;
        EditorStatusBar.IsVisible = false;
        EditorContentGrid.Children.Remove(FolderPanel);
        FolderPanel.DataContext = DataContext;
        FolderPanel.IsVisible = true;
        EditorContentGrid.ColumnDefinitions[0].Width = new GridLength(0);
        SidebarColumn.Width = new GridLength(0);
        SidebarSplitterColumn.Width = new GridLength(0);
    }

    private async void ProjectTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_projectLayout && _viewModel?.SelectedFolderNode is { IsDirectory: false, IsPlaceholder: false } node)
        {
            await _viewModel.OpenPathAsync(node.Path);
            e.Handled = true;
        }
    }

    private async void ProjectTree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_projectLayout && e.Key == Key.Enter && _viewModel?.SelectedFolderNode is { IsDirectory: false, IsPlaceholder: false } node)
        {
            await _viewModel.OpenPathAsync(node.Path);
            e.Handled = true;
        }
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
            SyncTextDocument();
            Editor.IsReadOnly = _viewModel.IsReadOnly || !_viewModel.IsTextDocument;
            UpdateSyntaxHighlighting(_viewModel.CurrentPath);
            UpdateSidebarLayout(_viewModel.IsSidebarVisible);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(CodeEditorViewModel.Text) or nameof(CodeEditorViewModel.ActiveDocument))
            SyncTextDocument();
        else if ((eventArgs.PropertyName == nameof(CodeEditorViewModel.IsReadOnly)
            || eventArgs.PropertyName == nameof(CodeEditorViewModel.IsTextDocument)) && _viewModel is not null)
        {
            Editor.IsReadOnly = _viewModel.IsReadOnly || !_viewModel.IsTextDocument;
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

    private void SyncTextDocument()
    {
        if (_viewModel is null) return;
        var document = _viewModel.ActiveDocument;
        if (!ReferenceEquals(_displayedDocument, document))
        {
            if (_displayedDocument is { } previous)
            {
                previous.CaretOffset = Editor.CaretOffset;
                previous.VerticalOffset = Editor.VerticalOffset;
                previous.HorizontalOffset = Editor.HorizontalOffset;
            }
            Editor.Document.TextChanged -= OnEditorTextChanged;
            Editor.Document = document is null ? new AvaloniaEdit.Document.TextDocument()
                : document.EditorBuffer ??= new AvaloniaEdit.Document.TextDocument(_viewModel.Text);
            _displayedDocument = document;
            Editor.Document.TextChanged += OnEditorTextChanged;
            Editor.CaretOffset = Math.Clamp(document?.CaretOffset ?? 0, 0, Editor.Document.TextLength);
            Editor.ScrollToVerticalOffset(document?.VerticalOffset ?? 0);
            Editor.ScrollToHorizontalOffset(document?.HorizontalOffset ?? 0);
        }
        if (Editor.Text != _viewModel.Text)
        {
            var caret = Editor.CaretOffset;
            Editor.Document.TextChanged -= OnEditorTextChanged;
            Editor.Text = _viewModel.Text;
            Editor.Document.UndoStack.ClearAll();
            Editor.Document.TextChanged += OnEditorTextChanged;
            Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);
        }
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
        if (_projectLayout) return;
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
