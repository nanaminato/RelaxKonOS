using RelaxKonOS.Client.Services.Auth;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// Builds the one navigation an application's settings surface offers. It resolves the host and login
/// when invoked, so a stale window cannot jump with an old session; it never creates an install entry.
/// </summary>
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
}
