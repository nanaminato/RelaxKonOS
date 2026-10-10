using System.Reflection;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;

public class SiteEditorSession : DispatchProxy
{
    public string Service = "https://test.invalid";
    public string Transport = "https://test.invalid";
    public UserDto User = new(Guid.NewGuid(), "test", HostPlatformKind.Linux, "test", DateTimeOffset.UtcNow, null);
    public SessionDto Session;
    private EventHandler<AuthSessionStateChangedEventArgs>? _changed;
    public SiteEditorSession()
    {
        var now = DateTimeOffset.UtcNow;
        Session = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, now, SessionStatus.Active, User.Id, "test", now);
    }
    public void ReplaceIdentity()
    {
        // Even identical DTO values returned by a new login are different authentication snapshots.
        User = User with { };
        Session = Session with { };
        _changed?.Invoke(this, new(AuthSessionState.Authenticated));
    }
    public void RebindTransport()
    {
        Transport = "https://rebound.test.invalid";
        _changed?.Invoke(this, new(AuthSessionState.Authenticated));
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_State": return AuthSessionState.Authenticated;
            case "get_ServiceId": return Service;
            case "get_EffectiveBaseUrl": return Transport;
            case "get_CurrentUser": return User;
            case "get_CurrentSession": return Session;
            case "add_StateChanged": _changed += (EventHandler<AuthSessionStateChangedEventArgs>)args![0]!; return null;
            case "remove_StateChanged": _changed -= (EventHandler<AuthSessionStateChangedEventArgs>)args![0]!; return null;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
