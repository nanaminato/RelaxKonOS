using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using RelaxKonOS.Client.Apps.FileServices.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Protocol.Installations;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Services.Installation;

public static class InstallationPanel
{
    public static InstallationTaskViewModel Create(AppContext context, InstallationServiceId service, string appId, Func<Task> refresh)
        => new((InstallationClient)context.Services.GetService(typeof(InstallationClient))!,
            (IAppSettingsClient)context.Services.GetService(typeof(IAppSettingsClient))!, service, appId, refresh,
            () => context.WindowManager.ShowSystemDialogAsync<string?>(LocalizedText.Get("installation.elevation_title"),
                dialog => new FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")), new RelaxKonOS.Core.Primitives.Size(460, 230)));

    public static Control Wrap(Control content, InstallationTaskViewModel model)
    {
        // A shared bottom status row keeps installation feedback out of each app's workspace.
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,38"), DataContext = model };
        panel.Bind(Visual.IsVisibleProperty, new Binding(nameof(model.HasMessage)));
        var log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 180, Margin = new Thickness(12, 8, 12, 4), FontSize = 12 };
        log.Bind(TextBox.TextProperty, new Binding(nameof(model.OperationLog)) { Mode = BindingMode.OneWay });
        log.Bind(Visual.IsVisibleProperty, new Binding(nameof(model.IsOperationLogExpanded)));
        panel.Children.Add(log);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(12, 0) };
        Grid.SetRow(row, 1); panel.Children.Add(row);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Width = 36, MinWidth = 0, Height = 3, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        progress.Bind(ProgressBar.ValueProperty, new Binding(nameof(model.Progress)));
        progress.Bind(ProgressBar.IsIndeterminateProperty, new Binding(nameof(model.IsIndeterminate)));
        progress.Bind(Visual.IsVisibleProperty, new Binding(nameof(model.IsActive))); row.Children.Add(progress);
        var stage = new TextBlock { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        stage.Bind(TextBlock.TextProperty, new Binding(nameof(model.SummaryText)));
        stage.Bind(ToolTip.TipProperty, new Binding(nameof(model.SummaryText)));
        Grid.SetColumn(stage, 1); row.Children.Add(stage);
        var toggle = new Avalonia.Controls.Primitives.ToggleButton { Content = LocalizedText.Get("common.operation_log"), Height = 28, Padding = new Thickness(8, 2) };
        toggle.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(model.IsOperationLogExpanded)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        var cancel = new Button { Content = LocalizedText.Get("installation.cancel"), Height = 28, Padding = new Thickness(8, 2) };
        cancel.Bind(Button.CommandProperty, new Binding(nameof(model.CancelCommand)));
        cancel.Bind(Visual.IsVisibleProperty, new Binding(nameof(model.IsActive)));
        Grid.SetColumn(cancel, 3); row.Children.Add(cancel);
        var root = new DockPanel(); DockPanel.SetDock(panel, Dock.Bottom); root.Children.Add(panel); root.Children.Add(content);
        root.AttachedToVisualTree += async (_, _) => await model.RestoreAsync();
        root.DetachedFromVisualTree += (_, _) => model.Dispose();
        return root;
    }
}
