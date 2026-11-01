using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public partial class ServerModulesView : UserControl
{
    private bool _compact;
    private bool _layoutApplied;
    private ServerModulesViewModel? _observed;

    public ServerModulesView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_observed is not null) _observed.PropertyChanged -= ModelChanged;
        _observed = DataContext as ServerModulesViewModel;
        if (_observed is not null) _observed.PropertyChanged += ModelChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_observed is null && DataContext is ServerModulesViewModel model)
        {
            _observed = model;
            model.PropertyChanged += ModelChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_observed is not null) _observed.PropertyChanged -= ModelChanged;
        _observed = null;
    }

    /// <summary>
    /// A frozen plan appears below the long module cards. Selecting an action at the top of the
    /// detail would otherwise create a plan that the user has to find by scrolling; bring the
    /// decision surface into view once it exists, after layout has made it visible.
    /// </summary>
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_observed is null) return;
        if (e.PropertyName == nameof(ServerModulesViewModel.HasPlan) && _observed.HasPlan)
            Dispatcher.UIThread.Post(() =>
            {
                if (PlanCard is { IsEffectivelyVisible: true }) PlanCard.BringIntoView();
            }, DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(ServerModulesViewModel.HasPackagePlan) && _observed.HasPackagePlan)
            Dispatcher.UIThread.Post(() =>
            {
                if (PackagePlanCard is { IsEffectivelyVisible: true }) PackagePlanCard.BringIntoView();
            }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// A module rail beside a detail pane needs roughly 900 px. Below that the two panes would each
    /// be too narrow to read a module id and its facts, so the list moves above the detail instead
    /// of shrinking both. The component-center workspace already removes its own navigation at the
    /// same width, which is why this page can use the full body width in compact mode.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyLayout(e.NewSize.Width);
    }

    private void ApplyLayout(double width)
    {
        if (width <= 0 || ModuleRail is null || ModuleDetail is null || ModulesLayout is null) return;
        var compact = width < 900;
        if (_layoutApplied && compact == _compact) return;
        _layoutApplied = true;
        _compact = compact;

        ModulesLayout.ColumnDefinitions.Clear();
        ModulesLayout.RowDefinitions.Clear();
        if (compact)
        {
            ModulesLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ModulesLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ModulesLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(ModuleRail, 0);
            Grid.SetRow(ModuleRail, 0);
            Grid.SetColumn(ModuleDetail, 0);
            Grid.SetRow(ModuleDetail, 1);
            ModuleList.MaxHeight = 360;
        }
        else
        {
            ModulesLayout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(316)));
            ModulesLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ModulesLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(ModuleRail, 0);
            Grid.SetRow(ModuleRail, 0);
            Grid.SetColumn(ModuleDetail, 1);
            Grid.SetRow(ModuleDetail, 0);
            ModuleList.MaxHeight = 500;
        }
    }

    /// <summary>
    /// A page-level tab changes what the body means, so it must not inherit the previous tab's
    /// reading position. The server-module page lives in the workspace's details scroller; reset
    /// that scroller instead of leaving the user halfway down a different section.
    /// </summary>
    private void SectionTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (this.FindAncestorOfType<ScrollViewer>() is { } scroller) scroller.Offset = new Vector(0, 0);
    }

    private async void InstallPackage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerModulesViewModel model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("server_modules.install_package"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("RelaxKonOS module") { Patterns = ["*.rkmodule"] }],
            });
            if (files.Count != 1) return;
            await using var stream = await files[0].OpenReadAsync();
            if (stream is not { CanSeek: true }) return;
            await model.InstallPackageAsync(stream, stream.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            model.PackageStatus = "server_modules.packages_unavailable";
        }
    }
}
