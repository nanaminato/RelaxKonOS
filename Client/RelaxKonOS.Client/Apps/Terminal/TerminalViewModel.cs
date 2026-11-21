using RelaxKonOS.App.Terminal;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Workspace;
using RoyalTerminal.Terminal;
using RelaxKonOS.Client.Services.ServerCenter;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// One view-model owns exactly one terminal session; a workspace can host several session tabs.
/// RoyalTerminal is the only desktop engine, so the view-model owns the transport lifetime and
/// hosts the single surface the factory builds.
/// </summary>
public partial class TerminalViewModel : TerminalTabViewModel
{
    private readonly RelaxKonOS.Client.Services.DesktopDevicePreferences? _devicePreferences;
    protected override void OnVtEngineEdited(string value) =>
        _devicePreferences?.Update(options => options with { TerminalVtEngine = value });
    private readonly RelaxKonOS.Client.Services.ClientPresentationPreferences? _preferences;
    public override RelaxKonOS.Protocol.Registry.TerminalPaddingSettings Padding => _preferences?.TerminalPadding ?? RelaxKonOS.Protocol.Registry.TerminalPaddingSettings.Default;
    private void OnPreferencesChanged(object? sender, EventArgs args)
    {
        if (_session?.State != AuthSessionState.Authenticated) return;
        OnPropertyChanged(nameof(Padding));
        if (_preferences is null || Appearance == _preferences.Terminal) return;
        ApplyAppearance(_preferences.Terminal);
        _surface?.ApplyAppearance(Appearance);
    }
    private const int Columns = 80;
    private const int Rows = 24;

    private readonly IAuthSession? _session;
    private readonly ITerminalSettingsClient _settingsClient;
    private readonly string? _initialSessionId;
    private string? _reservedSessionId;
    private readonly string? _initialWorkingDirectory;
    private readonly TerminalConnectionOptions _connectionOptions;
    private readonly SshDesktopSession? _sshDesktop;
    private TerminalSurfaceFactory? _surfaceFactory;
    private ITerminalSurface? _surface;
    private bool _attached;
    private long _attachmentGeneration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private LocalizedStatus _status = LocalizedText.Ref("terminal.status.ready");
    [ObservableProperty] private bool _hasExited;
    public override string DisplayTitle
    {
        get
        {
            if (!Title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !(Title.Contains(":\\", StringComparison.Ordinal) || Title.StartsWith('/'))) return Title;
            var name = Title[(Math.Max(Title.LastIndexOf('\\'), Title.LastIndexOf('/')) + 1)..];
            return IsAdministrator ? LocalizedText.Format("terminal.administrator_shell", name) : name;
        }
    }
    public bool IsAdministrator { get; }
    public override bool CanOpenAdministratorTerminal => _sshDesktop?.IsConnected != true
        && _session?.CurrentServer is { Platform: RelaxKonOS.Protocol.Common.HostPlatformKind.Windows,
            Host.Capabilities.PrivilegedOperations: true };

    /// <summary>The engine currently rendering this session. The view hosts <see cref="Surface"/>.</summary>
    public override ITerminalSurface? Surface => _surface;

    /// <summary>Raised after <see cref="Surface"/> was replaced; the view re-hosts the session control.</summary>
    public override event EventHandler? SurfaceChanged;

    /// <summary>
    /// True while closing the window would kill a live server PTY. Desktop teardown and logout
    /// bypass the window guard and run with an unauthenticated session, so the process survives
    /// and stays available for the next workspace restore — only a deliberate close prompts and
    /// kills it.
    /// </summary>
    public override bool HasLiveSession => _surface is not null && !HasExited && _session is { State: AuthSessionState.Authenticated };

    public TerminalViewModel(
        IAuthSession? session,
        ITerminalSettingsClient settingsClient,
        NetworkDiagnosticsService? diagnostics,
        SshDesktopSession? sshDesktop = null,
        string? initialSessionId = null,
        string? initialWorkingDirectory = null,
        bool isAdministrator = false,
        RelaxKonOS.Client.Services.ClientPresentationPreferences? preferences = null,
        RelaxKonOS.Client.Services.DesktopDevicePreferences? devicePreferences = null)
        : base(App.Services?.GetService<LocalizationService>(),
            (sshDesktop?.IsConnected == true ? sshDesktop.DevicePreferences : devicePreferences)?.Value.TerminalVtEngine ?? "Auto")
    {
        _devicePreferences = sshDesktop?.IsConnected == true ? sshDesktop.DevicePreferences : devicePreferences;
        _preferences = session is { State: AuthSessionState.Authenticated } && sshDesktop?.IsConnected != true ? preferences : null;
        _session = session;
        _settingsClient = settingsClient;
        _connectionOptions = new(session, sshDesktop, diagnostics);
        _sshDesktop = sshDesktop;
        _initialSessionId = initialSessionId;
        _reservedSessionId = initialSessionId;
        _initialWorkingDirectory = initialWorkingDirectory;
        IsAdministrator = isAdministrator;
        Title = LocalizedText.Get(isAdministrator ? "terminal.administrator_title" : "application.relaxkonos.terminal.display_name");
    }

    /// <summary>Called once when the terminal control is loaded.</summary>
    public override async Task AttachAsync()
    {
        if (_attached) return;
        _attached = true;
        var generation = ++_attachmentGeneration;

        if (_preferences is not null)
        {
            _preferences.Changed += OnPreferencesChanged;
            _preferences.Activate();
            try { await _preferences.EnsureLoadedAsync(); }
            catch { /* Keep the last valid appearance; connecting the PTY reports its own connection failure. */ }
            OnPreferencesChanged(null, EventArgs.Empty);
        }
        else await LoadAppearanceAsync();
        if (!_attached || generation != _attachmentGeneration) return;

        try
        {
            var sshDesktop = _sshDesktop ?? App.Services.GetRequiredService<SshDesktopSession>();
            _surfaceFactory = new TerminalSurfaceFactory(new SshDesktopCredentialProvider(sshDesktop),
                new RoyalTerminal.Terminal.Transport.Ssh.KnownHostsSshHostKeyValidator());
            CreateSurface();
        }
        catch (Exception ex)
        {
            ReleaseReservation();
            HasExited = true;
            Status = LocalizedText.Ref("terminal.status.start_failed", ex.Message);
            return;
        }
        await StartSessionAsync(_initialSessionId, generation);
    }

    /// <summary>
    /// A deliberate terminal-window close kills the corresponding server process. During workspace
    /// logout/shutdown the auth session is already unauthenticated, so only the connection closes and
    /// the PTY remains available for the next workspace restore.
    /// </summary>
    public override void Detach()
    {
        ReleaseReservation();
        if (!_attached) return;
        _attached = false;
        ++_attachmentGeneration;
        if (_preferences is not null)
        {
            _preferences.Changed -= OnPreferencesChanged;
            _preferences.Deactivate();
        }
        if (_surface is null) { _surfaceFactory = null; return; }

        var kill = _session is { State: AuthSessionState.Authenticated };
        ReleaseActiveSession(kill);
        TeardownSurface();
        _surfaceFactory = null;
    }

    public override string StatusText => Status.Resolve();

    private void CreateSurface()
    {
        if (_surfaceFactory is not { } factory)
            return;

        var engine = _devicePreferences?.Value.TerminalVtEngine ?? VtEngine;
        var surface = factory.Create(Enum.Parse<VtProcessorPreference>(engine));
        surface.ProcessExited += OnProcessExited;
        surface.TitleChanged += OnTitleChanged;
        surface.ApplyAppearance(Appearance);
        surface.ApplyLabels(SurfaceLabels());
        _surface = surface;
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TeardownSurface()
    {
        if (_surface is not { } surface)
            return;

        surface.ProcessExited -= OnProcessExited;
        surface.TitleChanged -= OnTitleChanged;
        surface.StopSession();
        surface.Dispose();
        _surface = null;
    }

    private async Task StartSessionAsync(string? sessionId, long generation)
    {
        if (_surface is not { } surface || _surfaceFactory is not { } factory)
            return;

        HasExited = false;
        var dimensions = new TerminalSessionDimensions(Columns, Rows, WidthPixels: 800, HeightPixels: 480);
        try
        {
            var (options, remote) = _connectionOptions.Create(dimensions, sessionId, _initialWorkingDirectory, IsAdministrator);
            Status = LocalizedText.Ref(remote ? "terminal.status.connecting" : "terminal.status.local_fallback");
            await surface.StartSessionAsync(options, CancellationToken.None);
            if (generation != _attachmentGeneration) return;
            var id = factory.TransportFactory.CurrentSessionId;
            if (id != _reservedSessionId)
            {
                ReleaseReservation();
                if (id is not null && TerminalSessionReservations.TryReserve(id)) _reservedSessionId = id;
            }
            Status = LocalizedText.Ref(IsAdministrator ? "terminal.status.administrator_connected" : "terminal.status.connected");
        }
        catch (Exception ex)
        {
            if (generation != _attachmentGeneration) return;
            ReleaseReservation();
            HasExited = true;
            Status = LocalizedText.Ref("terminal.status.start_failed", ex.Message);
        }
    }

    private void ReleaseActiveSession(bool kill)
    {
        if (_surfaceFactory is null)
            return;

        _ = kill ? _surfaceFactory.TransportFactory.KillActiveAsync() : _surfaceFactory.TransportFactory.StopActiveAsync();
    }

    private void ReleaseReservation()
    {
        if (_reservedSessionId is not { } id) return;
        _reservedSessionId = null;
        TerminalSessionReservations.Release(id);
    }

    private async Task LoadAppearanceAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, EffectiveBaseUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
            return;

        try
        {
            var settings = await _settingsClient.GetAsync(url, tokens.AccessToken, workspace.Id);
            ApplyAppearance(settings);
        }
        catch
        {
            // A terminal remains usable with defaults if an older server has no settings endpoint.
        }
    }

    protected override void OnAppearanceEdited(TerminalSettingsDto settings)
    {
        _surface?.ApplyAppearance(settings);
        _ = SaveAppearanceAsync(settings);
    }

    private async Task SaveAppearanceAsync(TerminalSettingsDto settings)
    {
        if (_session is not { State: AuthSessionState.Authenticated, EffectiveBaseUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
            return;

        try { await _settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id, settings); }
        catch { /* retain the local setting; a later change can retry */ }
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        HasExited = true;
        Status = exitCode == 0
            ? LocalizedText.Ref("terminal.status.process_exited")
            : LocalizedText.Ref("terminal.status.process_exited_with_code", exitCode);
    }

    private void OnTitleChanged(object? sender, string title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = IsAdministrator ? LocalizedText.Format("terminal.administrator_shell", title) : title;
        }
    }
}
