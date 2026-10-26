using Avalonia.Controls;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public static class ComponentCenterEntry
{
    public static Action Navigate(AppContext context, string appId, string componentId)
    {
        var session = (IAuthSession)context.Services.GetService(typeof(IAuthSession))!;
        var login = session.CurrentSession;
        var host = session.ServiceId;
        return () =>
        {
            if (host is not null && session.State == AuthSessionState.Authenticated && ReferenceEquals(login, session.CurrentSession) && host == session.ServiceId)
                context.Activations.Activate(ComponentCenterNavigation.Component(host, appId, componentId));
        };
    }
    private sealed class EntryText : LocalizedObservableObject
    {
        public string Text => LocalizedText.Get("component_center.manage_dependency");
    }
    public static Control Wrap(Control content, Action navigate)
    {
        var button = new Button { DataContext = new EntryText(), Margin = new Avalonia.Thickness(16, 8) };
        button.Bind(Button.ContentProperty, new Avalonia.Data.Binding(nameof(EntryText.Text)));
        button.Click += (_, _) => navigate();
        var panel = new DockPanel(); DockPanel.SetDock(button, Dock.Top);
        panel.Children.Add(button); panel.Children.Add(content);
        return panel;
    }
}
