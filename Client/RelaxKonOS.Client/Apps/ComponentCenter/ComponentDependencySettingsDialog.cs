using Avalonia.Controls;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// Settings surfaces that are not a page inside an application's workspace (the Project Editor is an
/// editor, not a tabbed console) host the same dependency area in a dialog. It is still observation
/// plus one navigation, never an installation entry.
/// </summary>
public static class ComponentDependencySettingsDialog
{
    public static Task ShowAsync(AppContext context, ManagedWindow owner, ComponentDependencySettingsViewModel dependency) =>
        context.ShowDialogAsync<bool?>(owner, LocalizedText.Get("common.settings"), dialog =>
        {
            var close = new Button { Content = LocalizedText.Get("common.close"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            close.Click += (_, _) => dialog.Close(true);
            var body = new ComponentDependencySettingsView { DataContext = dependency };
            var panel = new DockPanel { Margin = new Avalonia.Thickness(16), LastChildFill = true };
            DockPanel.SetDock(close, Dock.Bottom);
            panel.Children.Add(close);
            panel.Children.Add(new ScrollViewer
            {
                Content = body,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            });
            return panel;
        }, new Size(640, 440));
}
