using RelaxKonOS.Client.Services;
namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Detail routes share one editor and its existing persistence lifecycle.</summary>
public abstract class PersonalizationDetailPageViewModel : SettingsPageViewModel
{
    protected PersonalizationDetailPageViewModel(ShellSettings settings, PersonalizationPageViewModel editor) : base(settings, null) => Editor = editor;
    public PersonalizationPageViewModel Editor { get; }
}
public sealed class PersonalizationColorsPageViewModel(ShellSettings settings, PersonalizationPageViewModel editor) : PersonalizationDetailPageViewModel(settings, editor)
{
    public override string Route => "personalization/colors";
    public override string DisplayNameKey => "settings.colors_and_mode";
    public override string DisplayName => "Colors and mode";
}
public sealed class PersonalizationStylePageViewModel(ShellSettings settings, PersonalizationPageViewModel editor) : PersonalizationDetailPageViewModel(settings, editor)
{
    public override string Route => "personalization/style";
    public override string DisplayNameKey => "settings.system_style";
    public override string DisplayName => "System style";
}
public sealed class PersonalizationLayoutPageViewModel(ShellSettings settings, PersonalizationPageViewModel editor) : PersonalizationDetailPageViewModel(settings, editor)
{
    public override string Route => "personalization/layout";
    public override string DisplayNameKey => "settings.desktop_layout";
    public override string DisplayName => "Desktop layout";
}
public sealed class PersonalizationBackgroundPageViewModel(ShellSettings settings, PersonalizationPageViewModel editor) : PersonalizationDetailPageViewModel(settings, editor)
{
    public override string Route => "personalization/background";
    public override string DisplayNameKey => "settings.wallpaper";
    public override string DisplayName => "Background";
}
