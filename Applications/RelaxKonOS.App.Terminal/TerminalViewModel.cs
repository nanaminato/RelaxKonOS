using System.Globalization;
using RelaxKonOS.AppSDK;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.App.Terminal;

/// <summary>
/// One view-model owns exactly one terminal session; a workspace can host several session tabs.
/// RoyalTerminal is the only desktop engine, so the view-model owns the transport lifetime and
/// hosts the single surface the factory builds.
/// </summary>
public partial class TerminalViewModel : TerminalTabViewModel
{
    private readonly ITerminalDevicePreferences? _devicePreferences;
    protected override void OnVtEngineEdited(string value) =>
        _devicePreferences?.SetVtEngine(value);
    private readonly ITerminalWorkspacePreferences? _preferences;
    private IDisposable? _preferenceObservation;
    public override RelaxKonOS.Protocol.Registry.TerminalPaddingSettings Padding => _preferences?.Padding ?? RelaxKonOS.Protocol.Registry.TerminalPaddingSettings.Default;
    private void OnPreferencesChanged()
    {
        OnPropertyChanged(nameof(Padding));
        if (_preferences is null || Appearance == _preferences.Appearance) return;
        ApplyAppearance(_preferences.Appearance);
        Surface?.ApplyAppearance(Appearance);
    }

    private readonly IExternalTerminal _terminal;
    private readonly string? _initialSessionId;
    private string? _reservedSessionId;
    private readonly string? _initialWorkingDirectory;
    private IExternalTerminalSession? _connection;
    private bool _attached;
    private long _attachmentGeneration;

    private string _statusKey = "terminal.status.ready";
    private object?[] _statusArguments = [];

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        OnPropertyChanged(nameof(StatusText));
    }

    [ObservableProperty] private bool _hasExited;
    public override string DisplayTitle
    {
        get
        {
            if (!Title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !(Title.Contains(":\\", StringComparison.Ordinal) || Title.StartsWith('/'))) return Title;
            var name = Title[(Math.Max(Title.LastIndexOf('\\'), Title.LastIndexOf('/')) + 1)..];
            return IsAdministrator ? string.Format(CultureInfo.CurrentCulture, Label("terminal.administrator_shell"), name) : name;
        }
    }
    public bool IsAdministrator { get; }
    public override bool CanOpenAdministratorTerminal => _terminal.CanOpenAdministratorTerminal;

    public override ITerminalSurface? Surface => _connection?.Surface;

    /// <summary>Raised after <see cref="Surface"/> was replaced; the view re-hosts the session control.</summary>
    public override event EventHandler? SurfaceChanged;

    /// <summary>
    /// True while closing the window would kill a live server PTY. Desktop teardown and logout
    /// bypass the window guard and run with an unauthenticated session, so the process survives
    /// and stays available for the next workspace restore — only a deliberate close prompts and
    /// kills it.
    /// </summary>
    public override bool HasLiveSession => !HasExited && _connection?.HasAuthenticatedServer == true;

    public TerminalViewModel(
        ISystemLanguage? language,
        IExternalTerminal terminal,
        string? initialSessionId = null,
        string? initialWorkingDirectory = null,
        bool isAdministrator = false,
        ITerminalWorkspacePreferences? preferences = null,
        ITerminalDevicePreferences? devicePreferences = null)
        : base(language,
            devicePreferences?.VtEngine ?? "Auto")
    {
        _devicePreferences = devicePreferences;
        _preferences = preferences;
        _terminal = terminal;
        _initialSessionId = initialSessionId;
        _reservedSessionId = initialSessionId;
        _initialWorkingDirectory = initialWorkingDirectory;
        IsAdministrator = isAdministrator;
        Title = Label(isAdministrator ? "terminal.administrator_title" : "terminal.window_title");
    }

    /// <summary>Called once when the terminal control is loaded.</summary>
    public override async Task AttachAsync()
    {
        if (_attached) return;
        _attached = true;
        var generation = ++_attachmentGeneration;

        if (_preferences is not null)
        {
            _preferenceObservation = _preferences.Observe(OnPreferencesChanged);
            try { await _preferences.LoadAsync(); }
            catch { /* Keep the last valid appearance; connecting the PTY reports its own connection failure. */ }
            if (generation == _attachmentGeneration) OnPreferencesChanged();
        }
        if (!_attached || generation != _attachmentGeneration) return;

        try
        {
            CreateSurface();
        }
        catch (Exception ex)
        {
            ReleaseReservation();
            HasExited = true;
            SetStatus("terminal.status.start_failed", ex.Message);
            return;
        }
        await StartSessionAsync(generation);
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
        _preferenceObservation?.Dispose();
        _preferenceObservation = null;
        if (_connection is not { } connection) return;
        _connection = null;
        connection.Surface.ProcessExited -= OnProcessExited;
        connection.Surface.TitleChanged -= OnTitleChanged;
        connection.Dispose();
    }

    public override string StatusText => string.Format(CultureInfo.CurrentCulture, Label(_statusKey), _statusArguments);

    private void CreateSurface()
    {
        var engine = _devicePreferences?.VtEngine ?? VtEngine;
        _connection = _terminal.CreateSession(engine, _initialSessionId, _initialWorkingDirectory, IsAdministrator);
        var surface = _connection.Surface;
        surface.ProcessExited += OnProcessExited;
        surface.TitleChanged += OnTitleChanged;
        surface.ApplyAppearance(Appearance);
        surface.ApplyLabels(SurfaceLabels());
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task StartSessionAsync(long generation)
    {
        if (_connection is not { } connection) return;
        HasExited = false;
        try
        {
            SetStatus(connection.IsRemote ? "terminal.status.connecting" : "terminal.status.local_fallback");
            await connection.StartAsync(CancellationToken.None);
            if (generation != _attachmentGeneration) return;
            var id = connection.SessionId;
            if (id != _reservedSessionId)
            {
                ReleaseReservation();
                if (id is not null && TerminalSessionReservations.TryReserve(id)) _reservedSessionId = id;
            }
            SetStatus(IsAdministrator ? "terminal.status.administrator_connected" : "terminal.status.connected");
        }
        catch (Exception ex)
        {
            if (generation != _attachmentGeneration) return;
            ReleaseReservation();
            HasExited = true;
            SetStatus("terminal.status.start_failed", ex.Message);
        }
    }

    private void ReleaseReservation()
    {
        if (_reservedSessionId is not { } id) return;
        _reservedSessionId = null;
        TerminalSessionReservations.Release(id);
    }

    protected override void OnAppearanceEdited(TerminalSettingsDto settings)
    {
        Surface?.ApplyAppearance(settings);
        _ = SaveAppearanceAsync(settings);
    }

    private async Task SaveAppearanceAsync(TerminalSettingsDto settings)
    {
        if (_preferences is null) return;
        try { await _preferences.SaveAsync(settings); }
        catch { /* retain the local setting; a later change can retry */ }
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        HasExited = true;
        if (exitCode == 0) SetStatus("terminal.status.process_exited");
        else SetStatus("terminal.status.process_exited_with_code", exitCode);
    }

    private void OnTitleChanged(object? sender, string title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = IsAdministrator ? string.Format(CultureInfo.CurrentCulture, Label("terminal.administrator_shell"), title) : title;
        }
    }
}
