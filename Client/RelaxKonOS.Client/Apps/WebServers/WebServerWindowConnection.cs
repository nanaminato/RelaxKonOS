using Avalonia.Utilities;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.WebServers;

internal sealed class WebServerWindowConnection(IAuthSession session) : IExternalServerConnection
{
    private readonly string? _serviceId = session.ServiceId;
    private readonly object? _login = session.CurrentSession;
    private readonly object? _user = session.CurrentUser;
    private readonly bool _authenticated = session.State == AuthSessionState.Authenticated;
    public HostPlatformKind? Platform { get; } = session.CurrentServer?.Platform;

    public bool IsCurrent => _authenticated && _serviceId is { Length: > 0 }
        && _login is not null && _user is not null
        && session.State == AuthSessionState.Authenticated && session.ServiceId == _serviceId
        && ReferenceEquals(session.CurrentSession, _login) && ReferenceEquals(session.CurrentUser, _user);

    public IDisposable ObserveInvalidation(Action invalidated) => new Subscription(this, session, invalidated);

    private sealed class Subscription : IDisposable
    {
        private readonly WebServerWindowConnection _connection;
        private readonly IAuthSession _session;
        private Action? _invalidated;

        public Subscription(WebServerWindowConnection connection, IAuthSession session, Action invalidated)
        {
            _connection = connection;
            _session = session;
            _invalidated = invalidated;
            WeakEventHandlerManager.Subscribe<IAuthSession, AuthSessionStateChangedEventArgs, Subscription>(
                session, nameof(IAuthSession.StateChanged), OnSessionChanged);
            CheckConnection();
        }

        private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => CheckConnection();

        private void CheckConnection()
        {
            if (_connection.IsCurrent) return;
            Interlocked.Exchange(ref _invalidated, null)?.Invoke();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _invalidated, null);
            WeakEventHandlerManager.Unsubscribe<AuthSessionStateChangedEventArgs, Subscription>(
                _session, nameof(IAuthSession.StateChanged), OnSessionChanged);
        }
    }
}
