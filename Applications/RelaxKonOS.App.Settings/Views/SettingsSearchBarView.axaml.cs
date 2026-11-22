using Avalonia;
using Avalonia.Controls;

namespace RelaxKonOS.App.Settings.Views;

/// <summary>
/// The centre block of the Settings window's fused title bar. It owns the search field and nothing
/// else. The workspace places the block and handles its keyboard shortcuts.
/// </summary>
public partial class SettingsSearchBarView : UserControl
{
    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<SettingsSearchBarView, string?>(nameof(Query));
    public static readonly StyledProperty<string?> PromptProperty =
        AvaloniaProperty.Register<SettingsSearchBarView, string?>(nameof(Prompt));

    public string? Query { get => GetValue(QueryProperty); set => SetValue(QueryProperty, value); }
    public string? Prompt { get => GetValue(PromptProperty); set => SetValue(PromptProperty, value); }

    public SettingsSearchBarView() => InitializeComponent();

    /// <summary>Raised by the host view right before the field is focused, so an open overlay can close first.</summary>
    public Action? BeforeFocusSearch { get; set; }

    public TextBox SearchBox => HeaderSearchBox;

    /// <summary>Focuses the field and selects its current query.</summary>
    public void FocusSearch()
    {
        BeforeFocusSearch?.Invoke();
        HeaderSearchBox.Focus();
        HeaderSearchBox.SelectAll();
    }
}
