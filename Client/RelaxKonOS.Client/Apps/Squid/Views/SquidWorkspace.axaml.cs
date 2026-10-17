using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Squid.Views;

/// <summary>
/// Single-page Squid console. The AXAML owns the layout; this class only switches the card layout
/// between two columns and one column, and brings the rendered file into view once a preview exists.
/// </summary>
public partial class SquidWorkspace : UserControl
{
    private const double TwoColumnThreshold = 900;
    private SquidViewModel? _viewModel;
    private bool _narrow;

    public SquidWorkspace() => InitializeComponent();

    public SquidWorkspace(SquidViewModel viewModel) : this()
    {
        DataContext = viewModel;
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
    }

    /// <summary>Moves the identity and refresh command into the host window's title bar.</summary>
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as SquidViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    /// <summary>
    /// The publish actions are pinned, so a preview can be requested while the rendered file is
    /// still below the fold. Scroll it into view instead of leaving the result invisible.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SquidViewModel.HasPreview)) return;
        if (_viewModel is { HasPreview: true }) PreviewPanel.BringIntoView();
    }

    private void ApplyLayout(double width)
    {
        var narrow = width < TwoColumnThreshold;
        if (narrow == _narrow) return;
        _narrow = narrow;
        if (narrow)
        {
            CardLayout.ColumnDefinitions = new ColumnDefinitions("*");
            CardLayout.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
            CardLayout.ColumnSpacing = 0;
            Grid.SetRow(SecondaryColumn, 1); Grid.SetColumn(SecondaryColumn, 0);
            Grid.SetRow(PreviewPanel, 2); Grid.SetColumn(PreviewPanel, 0); Grid.SetColumnSpan(PreviewPanel, 1);
        }
        else
        {
            CardLayout.ColumnDefinitions = new ColumnDefinitions("*,*");
            CardLayout.RowDefinitions = new RowDefinitions("Auto,Auto");
            CardLayout.ColumnSpacing = 16;
            Grid.SetRow(SecondaryColumn, 0); Grid.SetColumn(SecondaryColumn, 1);
            Grid.SetRow(PreviewPanel, 1); Grid.SetColumn(PreviewPanel, 0); Grid.SetColumnSpan(PreviewPanel, 2);
        }
    }
}
