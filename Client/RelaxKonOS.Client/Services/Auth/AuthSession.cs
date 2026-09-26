using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>Holds the current authenticated session and the locally remembered RelaxKonOS connections.</summary>
public sealed class AuthSession : IAuthSession
{
    private readonly IRelaxKonOSClient _client;
    private readonly IRememberedSessionStore _rememberedSessionStore;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private ServerConnectionIdentity? _identity;

    public AuthSession(IRelaxKonOSClient client, IRememberedSessionStore rememberedSessionStore)
    {
        _client = client;
        _rememberedSessionStore = rememberedSessionStore;
    }

    public AuthSessionState State { get; private set; } = AuthSessionState.Unauthenticated;

    /// <summary>稳定登录身份。换隧道端口不改变它，因此不会制造新的登录记录。</summary>
    public string? ServiceId => _identity?.ServiceId;

    /// <summary>本次会话的实际 HTTP 地址；受管隧道换端口只更新它。</summary>
    public string? EffectiveBaseUrl => _identity?.EffectiveBaseUrl;

    public AuthTokens? Tokens { get; private set; }
    public UserDto? CurrentUser { get; private set; }
    public ServerDescriptorDto? CurrentServer { get; private set; }
    public WorkspaceDto? CurrentWorkspace { get; private set; }
    public SessionDto? CurrentSession { get; private set; }
    public DeviceDto? CurrentDevice { get; private set; }
    public DeviceRole AssignedRole { get; private set; } = DeviceRole.Observer;

    public event EventHandler<AuthSessionStateChangedEventArgs>? StateChanged;

    public async Task<LoginResponse> LoginAsync(
        ServerConnectionIdentity identity,
        LoginRequest request,
        bool rememberServer,
        bool rememberPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            if (State == AuthSessionState.Connecting)
                throw new InvalidOperationException("A login request is already in progress.");
            State = AuthSessionState.Connecting;
        }
        RaiseStateChanged();

        try
        {
            // Requests always go to the resolved transport address; the credential key never does.
            var response = await _client.LoginAsync(identity.EffectiveBaseUrl, request, ct);
            Apply(response, identity);

            RememberedProfileSaveResult? saveResult = null;
            if (rememberServer)
            {
                saveResult = await _rememberedSessionStore.UpsertAsync(
                    new SavedLoginProfile(identity.ServiceId, request.Identifier,
                        rememberPassword ? request.Password : null, DateTimeOffset.UtcNow), ct);
            }

            State = AuthSessionState.Authenticated;
            // Saving a local credential is best-effort: it must not turn a successful remote login into an error,
            // but the UI needs the outcome so it can explain why the password will not be prefilled next time.
            RaiseStateChanged(saveResult);
            return response;
        }
        catch
        {
            State = AuthSessionState.Unauthenticated;
            RaiseStateChanged();
            throw;
        }
    }

    public void UpdateConnection(ServerConnectionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            var previous = _identity
                ?? throw new InvalidOperationException("No server has been selected.");
            if (!ServerConnectionIdentityRules.PreservesIdentity(previous, identity))
                throw new InvalidOperationException("A transport rebind must not change the service identity.");
            _identity = identity;
        }
        RaiseStateChanged();
    }

    public async Task<IReadOnlyList<SavedLoginProfile>> GetSavedProfilesAsync(CancellationToken ct = default)
        => await _rememberedSessionStore.LoadAsync(ct);

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var url = EffectiveBaseUrl;
            var tokens = Tokens;
            if (url is null || tokens is null)
            {
                Reset(AuthSessionEndReason.UserSignedOut);
                return;
            }

            try { await _client.LogoutAsync(url, tokens.AccessToken, tokens.RefreshToken, ct); }
            finally { Reset(AuthSessionEndReason.UserSignedOut); }
        }
        finally { _refreshGate.Release(); }
    }

    public Task<bool> RefreshAsync(CancellationToken ct = default)
        => RefreshCoreAsync(force: true, rejectedAccessToken: null, ct);

    public async Task<string?> GetAccessTokenAsync(TimeSpan renewBefore, string? rejectedAccessToken = null,
        CancellationToken ct = default)
    {
        AuthTokens? tokens;
        lock (_gate)
            tokens = State == AuthSessionState.Authenticated ? Tokens : null;

        if (tokens is null)
            return null;

        var shouldRefresh = rejectedAccessToken is not null
            ? string.Equals(tokens.AccessToken, rejectedAccessToken, StringComparison.Ordinal)
            : tokens.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.Add(renewBefore);
        if (!shouldRefresh)
            return tokens.AccessToken;

        return await RefreshCoreAsync(force: false, rejectedAccessToken, ct)
            ? Tokens?.AccessToken
            : null;
    }

    private async Task<bool> RefreshCoreAsync(bool force, string? rejectedAccessToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            string? url;
            string? serviceId;
            AuthTokens? tokens;
            lock (_gate)
            {
                url = EffectiveBaseUrl;
                serviceId = ServiceId;
                tokens = State == AuthSessionState.Authenticated ? Tokens : null;
            }
            if (url is null || serviceId is null || tokens is null)
                return false;

            // Another request may have completed a refresh while this caller waited.
            if (rejectedAccessToken is not null)
            {
                if (!string.Equals(tokens.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
                    return true;
                // The server rejected this exact token; refresh even if its local expiry says otherwise.
            }
            else if (!force && tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return true;

            var refreshed = (await _client.RefreshAsync(url, tokens.RefreshToken, ct)).Tokens;
            lock (_gate)
            {
                // Logout or a new login wins over an in-flight refresh. A tunnel rebind keeps the same
                // login identity, so it must not discard a refresh the server already accepted.
                if (State != AuthSessionState.Authenticated
                    || !string.Equals(ServiceId, serviceId, StringComparison.Ordinal)
                    || !string.Equals(Tokens?.RefreshToken, tokens.RefreshToken, StringComparison.Ordinal))
                    return false;
                Tokens = refreshed;
            }
            return true;
        }
        catch (RelaxKonOSAuthException ex) when (ex.Status == 401)
        {
            Reset(AuthSessionEndReason.RefreshTokenInvalid);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // A transient connection failure must not discard an otherwise valid local session.
            return false;
        }
        finally { _refreshGate.Release(); }
    }

    private void Apply(LoginResponse response, ServerConnectionIdentity identity)
    {
        _identity = identity;
        Tokens = response.Tokens;
        CurrentUser = response.User;
        CurrentServer = response.Server;
        CurrentWorkspace = response.Workspace;
        CurrentSession = response.Session;
        CurrentDevice = response.Device;
        AssignedRole = response.AssignedRole;
    }

    private void Reset(AuthSessionEndReason endReason = AuthSessionEndReason.None)
    {
        _identity = null;
        Tokens = null;
        CurrentUser = null;
        CurrentServer = null;
        CurrentWorkspace = null;
        CurrentSession = null;
        CurrentDevice = null;
        AssignedRole = DeviceRole.Observer;
        State = AuthSessionState.Unauthenticated;
        RaiseStateChanged(endReason: endReason);
    }

    private void RaiseStateChanged(RememberedProfileSaveResult? rememberedProfileSaveResult = null,
        AuthSessionEndReason endReason = AuthSessionEndReason.None)
        => StateChanged?.Invoke(this, new AuthSessionStateChangedEventArgs(State, rememberedProfileSaveResult, endReason));
}