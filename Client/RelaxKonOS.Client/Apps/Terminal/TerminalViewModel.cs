using System.Collections.Concurrent;
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
public partial class TerminalViewModel : LocalizedObservableObject
{
    private readonly RelaxKonOS.Client.Services.DesktopDevicePreferences? _devicePreferences;
    public IReadOnlyList<string> VtEngines { get; } = ["Auto", "Managed", "Native"];
    [ObservableProperty] private string _vtEngine = "Auto";
    partial void OnVtEngineChanged(string value)
    {
        if (VtEngines.Contains(value))
            _devicePreferences?.Update(options => options with { TerminalVtEngine = value });
    }
    private readonly RelaxKonOS.Client.Services.ClientPresentationPreferences? _preferences;
    public RelaxKonOS.Protocol.Registry.TerminalPaddingSettings Padding => _preferences?.TerminalPadding ?? RelaxKonOS.Protocol.Registry.TerminalPaddingSettings.Default;
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
    private static readonly ConcurrentDictionary<string, byte> OpenSessions = new();

    private readonly IAuthSession? _session;
    private readonly ITerminalSettingsClient _settingsClient;
    private readonly string? _initialSessionId;
    private readonly string? _initialWorkingDirectory;
    private readonly NetworkDiagnosticsService? _diagnostics;
    private readonly SshDesktopSession? _sshDesktop;
    private TerminalSurfaceFactory? _surfaceFactory;
    private ITerminalSurface? _surface;
    private bool _attached;
    private long _attachmentGeneration;
    private bool _loadingAppearance;

    [ObservableProperty] private LocalizedStatus _status = LocalizedText.Ref("terminal.status.ready");
    [ObservableProperty] private bool _hasExited;
    [ObservableProperty] private string _title = string.Empty;
    public string DisplayTitle
    {
        get
        {
            if (!Title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !(Title.Contains(":\\", StringComparison.Ordinal) || Title.StartsWith('/'))) return Title;
            var name = Title[(Math.Max(Title.LastIndexOf('\\'), Title.LastIndexOf('/')) + 1)..];
            return IsAdministrator ? LocalizedText.Format("terminal.administrator_shell", name) : name;
        }
    }
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    [ObservableProperty] private TerminalSettingsDto _appearance = TerminalSettingsDto.Default;
    [ObservableProperty] private string _fontFamily = TerminalSettingsDto.Default.FontFamily;
    [ObservableProperty] private double _fontSize = TerminalSettingsDto.Default.FontSize;
    [ObservableProperty] private string _colorScheme = TerminalSettingsDto.Default.ColorScheme;

    public IReadOnlyList<string> FontFamilies => TerminalAppearance.FontFamilies;
    public IReadOnlyList<double> FontSizes => TerminalAppearance.FontSizes;
    public IReadOnlyList<string> ColorSchemes => TerminalAppearance.ColorSchemes;
    public Func<Task>? RequestSettingsAsync { get; set; }
    public Func<Task>? RequestEnvironmentAsync { get; set; }
    public Action? CloseSettingsAction { get; set; }
    public Action? RequestAdministratorTerminal { get; set; }
    public bool IsAdministrator { get; }
    public bool CanOpenAdministratorTerminal => _sshDesktop?.IsConnected != true
        && _session?.CurrentServer is { Platform: RelaxKonOS.Protocol.Common.HostPlatformKind.Windows,
            Host.Capabilities.PrivilegedOperations: true };

    /// <summary>The engine currently rendering this session. The view hosts <see cref="Surface"/>.</summary>
    public ITerminalSurface? Surface => _surface;

    /// <summary>Raised after <see cref="Surface"/> was replaced; the view re-hosts the session control.</summary>
    public event EventHandler? SurfaceChanged;

    /// <summary>
    /// True while closing the window would kill a live server PTY. Desktop teardown and logout
    /// bypass the window guard and run with an unauthenticated session, so the process survives
    /// and stays available for the next workspace restore — only a deliberate close prompts and
    /// kills it.
    /// </summary>
    public bool HasLiveSession => _surface is not null && !HasExited && _session is { State: AuthSessionState.Authenticated };

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
    {
        _devicePreferences = sshDesktop?.IsConnected == true ? sshDesktop.DevicePreferences : devicePreferences;
        _vtEngine = _devicePreferences?.Value.TerminalVtEngine ?? "Auto";
        _preferences = session is { State: AuthSessionState.Authenticated } && sshDesktop?.IsConnected != true ? preferences : null;
        _session = session;
        _settingsClient = settingsClient;
        _diagnostics = diagnostics;
        _sshDesktop = sshDesktop;
        _initialSessionId = initialSessionId;
        _initialWorkingDirectory = initialWorkingDirectory;
        IsAdministrator = isAdministrator;
        Title = LocalizedText.Get(isAdministrator ? "terminal.administrator_title" : "application.relaxkonos.terminal.display_name");
    }

    public static bool IsSessionOpen(string sessionId) => OpenSessions.ContainsKey(sessionId);

    /// <summary>Reserves a restored session before its view is attached, preventing duplicate windows.</summary>
    public static bool TryReserveSession(string sessionId) => OpenSessions.TryAdd(sessionId, 0);

    /// <summary>Called once when the terminal control is loaded.</summary>
    public async Task AttachAsync(TerminalSurfaceFactory surfaceFactory)
    {
        if (_attached) return;
        _attached = true;
        var generation = ++_attachmentGeneration;
        _surfaceFactory = surfaceFactory;

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
            CreateSurface();
        }
        catch (Exception ex)
        {
            HasExited = true;
            Status = LocalizedText.Ref("terminal.status.start_failed", ex.Message);
            return;
        }
        await StartSessionAsync(_initialSessionId);
    }

    /// <summary>
    /// A deliberate terminal-window close kills the corresponding server process. During workspace
    /// logout/shutdown the auth session is already unauthenticated, so only the connection closes and
    /// the PTY remains available for the next workspace restore.
    /// </summary>
    public void Detach()
    {
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

    /// <summary>Re-raises the localizable state so the open window re-renders after a language switch.</summary>
    public void RefreshLocalization() => OnPropertyChanged(nameof(Status));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task OpenSettingsAsync()
        => await (RequestSettingsAsync?.Invoke() ?? Task.CompletedTask);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task OpenEnvironmentAsync() => RequestEnvironmentAsync?.Invoke() ?? Task.CompletedTask;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenAdministratorTerminal() => RequestAdministratorTerminal?.Invoke();

    private void CreateSurface()
    {
        if (_surfaceFactory is not { } factory)
            return;

        var engine = _devicePreferences?.Value.TerminalVtEngine ?? VtEngine;
        var surface = factory.Create(Enum.Parse<VtProcessorPreference>(engine));
        surface.ProcessExited += OnProcessExited;
        surface.TitleChanged += OnTitleChanged;
        surface.ApplyAppearance(Appearance);
        surface.ApplyLabels(factory.Labels());
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

    private async Task StartSessionAsync(string? sessionId)
    {
        if (_surface is null || _surfaceFactory is null)
            return;

        HasExited = false;
        var dimensions = new TerminalSessionDimensions(Columns, Rows, WidthPixels: 800, HeightPixels: 480);
        ITerminalTransportOptions options;

        if (_sshDesktop is { IsConnected: true, Endpoint: { } endpoint, HostKeyFingerprint: { } fingerprint })
        {
            Status = LocalizedText.Ref("terminal.status.connecting");
            options = new SshTransportOptions(
                Endpoint: new SshEndpointOptions(endpoint.Host, endpoint.Port, endpoint.UserName),
                RequestPty: true,
                TerminalType: "xterm-256color",
                InitialCommand: null,
                Authentication: new SshAuthenticationOptions(
                    UsePassword: true,
                    PasswordSecretId: "ssh-desktop-session",
                    PrivateKeySecretIds: Array.Empty<string>(),
                    UseAgent: false),
                Dimensions: dimensions)
            { ExpectedHostKeyFingerprintSha256 = fingerprint };
        }
        else if (_session is { State: AuthSessionState.Authenticated, EffectiveBaseUrl: { } url, Tokens: { } })
        {
            Status = LocalizedText.Ref("terminal.status.connecting");
            options = new SignalRTransportOptions(
                hubUrl: url.TrimEnd('/') + "/hubs/terminals",
                dimensions: dimensions,
                tokenProvider: () => _session?.GetAccessTokenAsync(TimeSpan.FromMinutes(1)) ?? Task.FromResult<string?>(null),
                accessToken: _session.Tokens.AccessToken,
                sessionId: sessionId,
                workingDirectory: _initialWorkingDirectory,
                diagnostics: _diagnostics,
                isAdministrator: IsAdministrator);
        }
        else
        {
            Status = LocalizedText.Ref("terminal.status.local_fallback");
            options = new PtyTransportOptions(
                Command: null,
                WorkingDirectory: _initialWorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment: null,
                Dimensions: dimensions);
        }

        try
        {
            await _surface.StartSessionAsync(options, CancellationToken.None);
            if (_surfaceFactory.TransportFactory.CurrentSessionId is { } id)
                OpenSessions.TryAdd(id, 0);
            Status = LocalizedText.Ref(IsAdministrator ? "terminal.status.administrator_connected" : "terminal.status.connected");
        }
        catch (Exception ex)
        {
            HasExited = true;
            Status = LocalizedText.Ref("terminal.status.start_failed", ex.Message);
        }
    }

    private void ReleaseActiveSession(bool kill)
    {
        if (_surfaceFactory is null)
            return;

        if (_surfaceFactory.TransportFactory.CurrentSessionId is { } id)
            OpenSessions.TryRemove(id, out _);
        _ = kill ? _surfaceFactory.TransportFactory.KillActiveAsync() : _surfaceFactory.TransportFactory.StopActiveAsync();
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

    private void ApplyAppearance(TerminalSettingsDto settings)
    {
        _loadingAppearance = true;
        Appearance = settings;
        FontFamily = settings.FontFamily;
        FontSize = settings.FontSize;
        ColorScheme = settings.ColorScheme;
        _loadingAppearance = false;
    }

    partial void OnFontFamilyChanged(string value) => UpdateAppearance();
    partial void OnFontSizeChanged(double value) => UpdateAppearance();
    partial void OnColorSchemeChanged(string value) => UpdateAppearance(applyScheme: true);

    private void UpdateAppearance(bool applyScheme = false)
    {
        if (_loadingAppearance)
            return;

        // The stored engine is always the built-in one, so a workspace written by an older build
        // (which could still hold the legacy `xterm` token) migrates on its next appearance write.
        var updated = Appearance with
        {
            FontFamily = FontFamily,
            FontSize = FontSize,
            ColorScheme = ColorScheme,
            Backend = TerminalBackends.BuiltIn,
        };
        Appearance = applyScheme ? TerminalAppearance.ApplyScheme(updated, ColorScheme) : updated;
        _surface?.ApplyAppearance(Appearance);
        _ = SaveAppearanceAsync(Appearance);
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
