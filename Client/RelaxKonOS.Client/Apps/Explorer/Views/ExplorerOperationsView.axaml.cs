using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Services;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Models;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

public partial class ExplorerOperationsView : UserControl
{
    private LocalizationService? _localization;
    private readonly DispatcherTimer _measurementTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public ExplorerOperationsView()
    {
        InitializeComponent();
        _measurementTimer.Tick += (_, _) => RefreshMeasurements();
    }
    private void RefreshMeasurements()
    {
        if (DataContext is not ExplorerOperationCenter center) return;
        foreach (var card in center.Jobs) card.RefreshMeasurements();
        // The band's total follows the same samples as the cards, not a second timer.
        center.RefreshAggregate();
    }
    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e) => RefreshMeasurements();
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _localization = App.Services?.GetService(typeof(LocalizationService)) as LocalizationService;
        if (_localization is not null) _localization.LanguageChanged += OnLanguageChanged;
        _measurementTimer.Start();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _measurementTimer.Stop();
        if (_localization is not null) _localization.LanguageChanged -= OnLanguageChanged;
        _localization = null;
        base.OnDetachedFromVisualTree(e);
    }
}
