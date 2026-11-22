using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

/// <summary>
/// The leading block of the Settings window's fused title bar.
/// </summary>
public partial class SettingsHeaderView : UserControl
{
    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<SettingsHeaderView, ICommand?>(nameof(BackCommand));
    public static readonly StyledProperty<string?> BackLabelProperty =
        AvaloniaProperty.Register<SettingsHeaderView, string?>(nameof(BackLabel));
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsHeaderView, string?>(nameof(Title));

    public ICommand? BackCommand { get => GetValue(BackCommandProperty); set => SetValue(BackCommandProperty, value); }
    public string? BackLabel { get => GetValue(BackLabelProperty); set => SetValue(BackLabelProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public SettingsHeaderView() => InitializeComponent();

    /// <summary>The back action; also the element the title bar's leading edge is measured from.</summary>
    public Button BackButton => HeaderBackButton;

    /// <summary>The window's own title. Hidden once the bar is too narrow to carry it.</summary>
    public TextBlock TitleText => HeaderTitle;
}
