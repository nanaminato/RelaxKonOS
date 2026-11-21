using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.Git.Views;

/// <summary>
/// The diff surface: path, change counts, the truncation notice and the patch itself.
/// </summary>
/// <remarks>
/// It exists so the modal dialog and the log page's inline pane are the same view of the same data.
/// The pane has no way to close itself — the host decides whether that belongs to it (an ✕ in the
/// header) or to its own chrome (the dialog's Close button).
/// </remarks>
public partial class GitDiffPane : UserControl
{
    /// <summary>Whether the pane shows its own close mark.</summary>
    public static readonly StyledProperty<bool> CanCloseProperty =
        AvaloniaProperty.Register<GitDiffPane, bool>(nameof(CanClose));

    public static readonly StyledProperty<string?> CloseLabelProperty =
        AvaloniaProperty.Register<GitDiffPane, string?>(nameof(CloseLabel));

    /// <summary>Raised when the close mark is clicked. The host decides what closing means.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CloseRequestedEvent =
        RoutedEvent.Register<GitDiffPane, RoutedEventArgs>(nameof(CloseRequested), RoutingStrategies.Bubble);

    public GitDiffPane() => InitializeComponent();

    public bool CanClose
    {
        get => GetValue(CanCloseProperty);
        set => SetValue(CanCloseProperty, value);
    }

    public event EventHandler<RoutedEventArgs> CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    public string? CloseLabel
    {
        get => GetValue(CloseLabelProperty);
        set => SetValue(CloseLabelProperty, value);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CloseRequestedEvent));
}
