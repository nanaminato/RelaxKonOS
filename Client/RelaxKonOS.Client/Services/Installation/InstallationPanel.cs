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
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8), DataContext = model };
        panel.Bind(Visual.IsVisibleProperty, new Binding(nameof(model.HasMessage)));
        var stage = new TextBlock(); stage.Bind(TextBlock.TextProperty, new Binding(nameof(model.StageText))); panel.Children.Add(stage);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4 };
        progress.Bind(ProgressBar.ValueProperty, new Binding(nameof(model.Progress)));
        progress.Bind(ProgressBar.IsIndeterminateProperty, new Binding(nameof(model.IsIndeterminate)));
        progress.Bind(Visual.IsVisibleProperty, new Binding(nameof(model.IsActive))); panel.Children.Add(progress);
        var connection = new TextBlock(); connection.Bind(TextBlock.TextProperty, new Binding(nameof(model.ConnectionText))); panel.Children.Add(connection);
        var cancel = new Button { Content = LocalizedText.Get("installation.cancel") };
        cancel.Bind(Button.CommandProperty, new Binding(nameof(model.CancelCommand)));
        cancel.Bind(Visual.IsVisibleProperty, new Binding(nameof(model.IsActive))); panel.Children.Add(cancel);
        var root = new DockPanel(); DockPanel.SetDock(panel, Dock.Top); root.Children.Add(panel); root.Children.Add(content);
        root.AttachedToVisualTree += async (_, _) => await model.RestoreAsync();
        root.DetachedFromVisualTree += (_, _) => model.Dispose();
        return root;
    }
}
