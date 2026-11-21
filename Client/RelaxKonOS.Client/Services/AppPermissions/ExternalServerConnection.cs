using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ExternalServerConnection(UsageMemoryScope scope) : IExternalServerConnection
{
    private readonly string? _server = scope.Session.ServiceId;
    private readonly bool _authenticated = scope.Session.State == AuthSessionState.Authenticated;
    internal IAuthSession Session => scope.Session;
    public HostPlatformKind? Platform => IsCurrent ? scope.Session.CurrentServer?.Platform : null;

    public IReadOnlyList<string> Capabilities => IsCurrent ? scope.Session.CurrentServer?.Capabilities.ToArray() ?? [] : [];

    public bool IsCurrent => _authenticated && _server is not null && scope.Session.State == AuthSessionState.Authenticated
        && scope.Session.ServiceId == _server && scope.IsCurrent;

    public IDisposable ObserveInvalidation(Action invalidated) => new Subscription(this, scope.Session, invalidated);

    private sealed class Subscription : IDisposable
    {
        private readonly ExternalServerConnection _connection;
        private readonly IAuthSession _session;
        private Action? _invalidated;

        public Subscription(ExternalServerConnection connection, IAuthSession session, Action invalidated)
        {
            _connection = connection;
            _session = session;
            _invalidated = invalidated;
            _session.StateChanged += OnStateChanged;
            CheckConnection();
        }

        private void OnStateChanged(object? sender, AuthSessionStateChangedEventArgs args) => CheckConnection();

        private void CheckConnection()
        {
            if (_connection.IsCurrent) return;
            var invalidated = Interlocked.Exchange(ref _invalidated, null);
            if (invalidated is null) return;
            _session.StateChanged -= OnStateChanged;
            invalidated();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _invalidated, null) is not null)
                _session.StateChanged -= OnStateChanged;
        }
    }
}
