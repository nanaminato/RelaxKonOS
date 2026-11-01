using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Squid.Views;

/// <summary>Squid workspace with persistent draft fields and compact page navigation.</summary>
public partial class SquidWorkspace : UserControl
{
    private SquidViewModel? _viewModel;

    public Func<string, Control, Task>? ShowEditorAsync { get; set; }

    private async void EditorButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } button || ShowEditorAsync is null) return;
        var parts = target.Split(':');
        if (parts.Length > 1 && _viewModel is { } accountModel)
        { accountModel.SelectedAccount = null; accountModel.NewAccountName = ""; accountModel.AccountPassword = ""; }
        // The trigger says "编辑…"; the dialog is titled after the card it edits.
        var title = button.CommandParameter?.ToString() ?? button.Content?.ToString() ?? "";
        await RelaxKonOS.Client.Controls.WorkspaceEditorDialogView.OpenAsync(this, parts[0], title, ShowEditorAsync);
        if (_viewModel is { } model) model.AccountPassword = "";
    }

    /// <summary>The adoption notice can only be resolved where the checkbox and apply button live.</summary>
    private void GoToPublishButton_Click(object? sender, RoutedEventArgs e) => ShowPage(4);

    public SquidWorkspace()
    {
        InitializeComponent();
        RelaxKonOS.Client.Controls.CompactWorkspaceNavigation.Install(this, WorkspaceBody);
        ShowPage(0);
    }

    public SquidWorkspace(SquidViewModel viewModel) : this() => DataContext = viewModel;

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var page)) ShowPage(page);
    }

    private void ShowPage(int page)
    {
        for (var i = 0; i < 7; i++)
        {
            this.FindControl<StackPanel>($"Page{i}")!.IsVisible = i == page;
            this.FindControl<Button>($"PageButton{i}")!.Classes.Set("nav-selected", i == page);
        }
        if (page == 6 && _viewModel?.Dependency is { } dependency) _ = dependency.RefreshAsync();
        BodyScroll.Offset = default;
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SquidViewModel.PreviewText) && _viewModel is { HasPreview: true })
            ShowPage(4);
    }
}
