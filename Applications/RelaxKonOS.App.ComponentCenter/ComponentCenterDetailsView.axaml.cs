using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentCenterDetailsView : UserControl
{
    public static readonly StyledProperty<Control?> ModulesViewProperty =
        AvaloniaProperty.Register<ComponentCenterDetailsView, Control?>(nameof(ModulesView));

    public static readonly StyledProperty<Control?> NginxViewProperty =
        AvaloniaProperty.Register<ComponentCenterDetailsView, Control?>(nameof(NginxView));

    public static readonly StyledProperty<Control?> RuntimeViewProperty =
        AvaloniaProperty.Register<ComponentCenterDetailsView, Control?>(nameof(RuntimeView));

    public ComponentCenterDetailsView() => InitializeComponent();

    public Control? ModulesView
    {
        get => GetValue(ModulesViewProperty);
        set => SetValue(ModulesViewProperty, value);
    }

    public Control? NginxView
    {
        get => GetValue(NginxViewProperty);
        set => SetValue(NginxViewProperty, value);
    }

    public Control? RuntimeView
    {
        get => GetValue(RuntimeViewProperty);
        set => SetValue(RuntimeViewProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? OpenApplicationRequested;

    private void OpenApplication_Click(object? sender, RoutedEventArgs e) =>
        OpenApplicationRequested?.Invoke(this, e);
}

public interface IComponentCenterDetails
{
    bool ShowServerModules { get; }
    bool ShowCatalogSources { get; }
    ICatalogSourcesViewModel? CatalogSources { get; }
    bool ShowComponentBrowser { get; }
    bool HasComponentDetails { get; }
    bool ShowDesktopPackage { get; }
    bool ShowNginxSelection { get; }
    bool ShowGitInstallation { get; }
    bool ShowNotIntegrated { get; }
    bool ShowHostObservation { get; }
    bool ShowOfficialDownloads { get; }
    bool ShowPackageFreeze { get; }
    bool HasRuntimeDetails { get; }
    bool HasResults { get; }
}
