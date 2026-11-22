using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ProxyServer.Views;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed class ComponentCenterTasksView : UserControl
{
    internal ComponentCenterTasksView(ComponentCenterViewModel model, Action? requestClose,
        Func<ComponentTaskDialogPresentation, Control> createLayout)
    {
        DataContext = model;
        var runtime = new ProxyServerRuntimeView();
        runtime.Bind(DataContextProperty, new Binding(nameof(ComponentCenterViewModel.DetailRuntime)) { Source = model });
        Content = createLayout(new(model, runtime, requestClose));

        UpdateRuntime();
        AttachedToVisualTree += (_, _) =>
        {
            model.PropertyChanged += ModelChanged;
            UpdateRuntime();
        };
        DetachedFromVisualTree += (_, _) => model.PropertyChanged -= ModelChanged;

        void ModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ComponentCenterViewModel.DetailRuntime) or nameof(ComponentCenterViewModel.ShowTaskDomainProgress))
                UpdateRuntime();
        }

        void UpdateRuntime() => runtime.ShowCenterSection(ComponentCenterPage.Tasks, !model.ShowTaskDomainProgress);
    }
}
