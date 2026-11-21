using Avalonia.Controls;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Docker.Views;

public partial class DockerLoginRequiredView : UserControl
{
    public DockerLoginRequiredView() => InitializeComponent();

    public DockerLoginRequiredView(DockerText text, ISystemLanguage language) : this()
    {
        void UpdateLabels() => DataContext = new { Labels = text };
        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => UpdateLabels();
        UpdateLabels();
        AttachedToVisualTree += (_, _) =>
        {
            UpdateLabels();
            language.LanguageChanged += LanguageChanged;
        };
        DetachedFromVisualTree += (_, _) => language.LanguageChanged -= LanguageChanged;
    }
}
