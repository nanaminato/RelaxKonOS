using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Settings.Views;

/// <summary>
/// The leading block of the Settings window's fused title bar. Keyboard navigation for the whole
/// title bar lives in <see cref="SettingsView"/>, which owns both blocks and knows the view model.
/// </summary>
public partial class SettingsHeaderView : UserControl
{
    public SettingsHeaderView() => InitializeComponent();

    /// <summary>The back action; also the element the title bar's leading edge is measured from.</summary>
    public Button BackButton => HeaderBackButton;

    /// <summary>The window's own title. Hidden once the bar is too narrow to carry it.</summary>
    public TextBlock TitleText => HeaderTitle;
}
