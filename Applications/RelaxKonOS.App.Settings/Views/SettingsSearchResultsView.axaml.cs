using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace RelaxKonOS.App.Settings.Views;

public partial class SettingsSearchResultsView : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<SettingsSearchEntry>?> ResultsProperty =
        AvaloniaProperty.Register<SettingsSearchResultsView, IReadOnlyList<SettingsSearchEntry>?>(nameof(Results));
    public static readonly StyledProperty<ICommand?> OpenResultCommandProperty =
        AvaloniaProperty.Register<SettingsSearchResultsView, ICommand?>(nameof(OpenResultCommand));
    public static readonly StyledProperty<string?> CatalogProblemProperty =
        AvaloniaProperty.Register<SettingsSearchResultsView, string?>(nameof(CatalogProblem));
    public static readonly StyledProperty<bool> HasNoResultsProperty =
        AvaloniaProperty.Register<SettingsSearchResultsView, bool>(nameof(HasNoResults));
    public static readonly StyledProperty<string?> NoResultsLabelProperty =
        AvaloniaProperty.Register<SettingsSearchResultsView, string?>(nameof(NoResultsLabel));
    public static readonly StyledProperty<string?> ResultsLabelProperty =
        AvaloniaProperty.Register<SettingsSearchResultsView, string?>(nameof(ResultsLabel));

    public IReadOnlyList<SettingsSearchEntry>? Results { get => GetValue(ResultsProperty); set => SetValue(ResultsProperty, value); }
    public ICommand? OpenResultCommand { get => GetValue(OpenResultCommandProperty); set => SetValue(OpenResultCommandProperty, value); }
    public string? CatalogProblem { get => GetValue(CatalogProblemProperty); set => SetValue(CatalogProblemProperty, value); }
    public bool HasNoResults { get => GetValue(HasNoResultsProperty); set => SetValue(HasNoResultsProperty, value); }
    public string? NoResultsLabel { get => GetValue(NoResultsLabelProperty); set => SetValue(NoResultsLabelProperty, value); }
    public string? ResultsLabel { get => GetValue(ResultsLabelProperty); set => SetValue(ResultsLabelProperty, value); }

    public SettingsSearchResultsView() => InitializeComponent();

    private void OnDoubleTapped(object? sender, TappedEventArgs args) => OpenSelectedResult(sender);

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        OpenSelectedResult(sender);
        args.Handled = true;
    }

    private void OpenSelectedResult(object? sender)
    {
        if (sender is not ListBox { SelectedItem: SettingsSearchEntry entry } list
            || OpenResultCommand is not { } command || !command.CanExecute(entry)) return;
        list.SelectedItem = null;
        command.Execute(entry);
    }
}
