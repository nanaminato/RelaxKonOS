using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Settings.Views;

/// <summary>
/// The centre block of the Settings window's fused title bar. It owns the search field and nothing
/// else: the host decides where the block lands, and <see cref="SettingsView"/> decides when the
/// shortcut keys reach it.
/// </summary>
public partial class SettingsSearchBarView : UserControl
{
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
