using System.ComponentModel;
using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.Views;

/// <summary>
/// Application-deployment shell. The layout lives in AXAML and binds straight to the view model; this
/// class only attaches the data context and decides whether a narrow window shows the list or the
/// selected application, because a 300px list column on a 560px window leaves nothing to read.
/// </summary>
internal partial class ApplicationDeploymentsWorkspace : UserControl
{
    private const double CompactWidth = 900;

    private ApplicationDeploymentsViewModel? _viewModel;
    private bool _compact;
    private bool _showDetail;

    public ApplicationDeploymentsWorkspace()
    {
        InitializeComponent();
        SizeChanged += (_, e) => { _compact = e.NewSize.Width > 0 && e.NewSize.Width < CompactWidth; ApplyLayout(); };
    }

    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window);
    }

    public static ApplicationDeploymentsWorkspace Create(ApplicationDeploymentsViewModel viewModel) =>
        new ApplicationDeploymentsWorkspace { DataContext = viewModel };

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as ApplicationDeploymentsViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ApplicationDeploymentsViewModel.HasSelection)
            or nameof(ApplicationDeploymentsViewModel.SelectedApplication))) return;
        // Choosing an application is the one navigation this workspace has; in a narrow window it has
        // to move to the detail page on its own, or the selection would appear to do nothing.
        _showDetail = _viewModel?.HasSelection == true;
        ApplyLayout();
    }

    private void BackToList_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _showDetail = false;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (_viewModel is null || ListPane is null || DetailHost is null) return;
        var hasSelection = _viewModel.HasSelection;
        var detailPage = _showDetail && hasSelection;

        ListPane.IsVisible = !_compact || !detailPage;
        DetailHost.IsVisible = !_compact || detailPage;
        BackToListButton.IsVisible = _compact && hasSelection;
        WorkspaceSplit.ColumnDefinitions = _compact ? new ColumnDefinitions("*") : new ColumnDefinitions("300,*");
        Grid.SetColumn(ListPane, 0);
        Grid.SetColumn(DetailHost, _compact ? 0 : 1);
    }
}
