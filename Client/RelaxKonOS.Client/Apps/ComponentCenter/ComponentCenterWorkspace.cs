using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using RelaxKonOS.App.ComponentCenter;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ProxyServer.Views;
using RelaxKonOS.Client.Apps.WebServers.Views;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed class ComponentCenterWorkspace : UserControl
{
    /// <summary>Receives the owning application id of the selected component.</summary>
    public Action<string>? OpenApplication { get; set; }

    public ComponentCenterWorkspace(ComponentCenterViewModel model)
        : this(model, ComponentCenterLayout.Create, () => new ServerModuleSecurityView()) { }

    internal ComponentCenterWorkspace(ComponentCenterViewModel model, Func<ComponentCenterPresentation, Control> createView,
        Func<Control> createModuleSecurityView)
    {
        DataContext = model;

        var modules = new ServerModulesView(createModuleSecurityView()) { DataContext = model.ServerModules };
        var nginx = new ContentControl
        {
            Content = model.NginxSelection,
            ContentTemplate = new FuncDataTemplate<object>((_, _) => new CurrentNginxSelectionView())
        };
        var runtime = new ProxyServerRuntimeView();
        runtime.Bind(DataContextProperty, new Binding(nameof(ComponentCenterViewModel.DetailRuntime)) { Source = model });
        runtime.ShowCenterSection(model.SelectedPage, false);

        var layout = createView(new(model, modules, nginx, runtime, () =>
        {
            if (model.SelectedApplicationId is { } application)
                OpenApplication?.Invoke(application);
        }));

        var names = new NameScope();
        names.Register("WorkspaceLayout", layout);
        names.Complete();
        NameScope.SetNameScope(this, names);
        Content = layout;

        AttachedToVisualTree += (_, _) =>
        {
            model.PropertyChanged += ModelChanged;
            runtime.ShowCenterSection(model.SelectedPage, false);
        };
        DetachedFromVisualTree += (_, _) => model.PropertyChanged -= ModelChanged;

        void ModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ComponentCenterViewModel.SelectedPage) or nameof(ComponentCenterViewModel.ShowTaskDomainProgress))
                runtime.ShowCenterSection(model.SelectedPage, false);
        }
    }
}
