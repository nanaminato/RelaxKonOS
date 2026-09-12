using System.Collections.Concurrent;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Workspace;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// One view-model owns exactly one terminal session. Session switching is intentionally absent:
/// each server PTY is represented by its own desktop window, like Windows Terminal.
/// </summary>
public partial class TerminalViewModel : LocalizedObservableObject
{
    private const int Columns = 80;
    private const int Rows = 24;
    private static readonly ConcurrentDictionary<string, byte> OpenSessions = new();

    private readonly IAuthSession? _session;
    private readonly ITerminalSettingsClient _settingsClient;
    private readonly string? _initialSessionId;
    private readonly string? _initialWorkingDirectory;
    private readonly NetworkDiagnosticsService? _diagnostics;
    private TerminalControl? _terminal;
    private SignalRTransportFactory? _transportFactory;
    private bool _loadingAppearance;

    [ObservableProperty] private LocalizedStatus _status = LocalizedText.Ref("terminal.status.ready");
    [ObservableProperty] private bool _hasExited;
    [ObservableProperty] private TerminalSettingsDto _appearance = TerminalSettingsDto.Default;
    [ObservableProperty] private string _fontFamily = TerminalSettingsDto.Default.FontFamily;
    [ObservableProperty] private double _fontSize = TerminalSettingsDto.Default.FontSize;
    [ObservableProperty] private string _colorScheme = TerminalSettingsDto.Default.ColorScheme;

    public IReadOnlyList<string> FontFamilies => TerminalAppearance.FontFamilies;
    public IReadOnlyList<double> FontSizes => TerminalAppearance.FontSizes;
    public IReadOnlyList<string> ColorSchemes => TerminalAppearance.ColorSchemes;
    public Func<Task>? RequestSettingsAsync { get; set; }
    public Action? CloseSettingsAction { get; set; }

    public TerminalViewModel(
        IAuthSession? session,
        ITerminalSettingsClient settingsClient,
        NetworkDiagnosticsService? diagnostics,
        string? initialSessionId = null,
        string? initialWorkingDirectory = null)
    {
        _session = session;
        _settingsClient = settingsClient;
        _diagnostics = diagnostics;
        _initialSessionId = initialSessionId;
        _initialWorkingDirectory = initialWorkingDirectory;
    }

    public static bool IsSessionOpen(string sessionId) => OpenSessions.ContainsKey(sessionId);

    /// <summary>Reserves a restored session before its view is attached, preventing duplicate windows.</summary>
    public static bool TryReserveSession(string sessionId) => OpenSessions.TryAdd(sessionId, 0);

    /// <summary>Called once when the terminal control is loaded.</summary>
    public async Task AttachAsync(TerminalControl terminal, SignalRTransportFactory transportFactory)
    {
        _terminal = terminal;
        _transportFactory = transportFactory;
        _terminal.ProcessExited += OnProcessExited;
        _terminal.TitleChanged += OnTitleChanged;

        await LoadAppearanceAsync();
        await StartSessionAsync(_initialSessionId);
    }

    /// <summary>
    /// A deliberate terminal-window close kills the corresponding server process. During workspace
    /// logout/shutdown the auth session is already unauthenticated, so only the connection closes and
    /// the PTY remains available for the next workspace restore.
    /// </summary>
    public void Detach()
    {
        if (_terminal is null)
            return;

        _terminal.ProcessExited -= OnProcessExited;
        _terminal.TitleChanged -= OnTitleChanged;

        var kill = _session is { State: AuthSessionState.Authenticated };
        ReleaseActiveSession(kill);
        try { _terminal.StopPty(); } catch { /* best effort */ }

        _terminal = null;
        _transportFactory = null;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task OpenSettingsAsync()
        => await (RequestSettingsAsync?.Invoke() ?? Task.CompletedTask);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

    private async Task StartSessionAsync(string? sessionId)
    {
        if (_terminal is null)
            return;

        HasExited = false;
        var dimensions = new TerminalSessionDimensions(Columns, Rows, WidthPixels: 800, HeightPixels: 480);
        ITerminalTransportOptions options;

        if (_session is { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } })
        {
            Status = LocalizedText.Ref("terminal.status.connecting");
            options = new SignalRTransportOptions(
                hubUrl: url.TrimEnd('/') + "/hubs/terminals",
                dimensions: dimensions,
                tokenProvider: () => _session?.GetAccessTokenAsync(TimeSpan.FromMinutes(1)) ?? Task.FromResult<string?>(null),
                accessToken: _session.Tokens.AccessToken,
                sessionId: sessionId,
                workingDirectory: _initialWorkingDirectory,
                diagnostics: _diagnostics);
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
            await _terminal.StartSessionAsync(options, CancellationToken.None);
            if (_transportFactory?.CurrentSessionId is { } id)
                OpenSessions.TryAdd(id, 0);
            Status = LocalizedText.Ref("terminal.status.connected");
        }
        catch (Exception ex)
        {
            HasExited = true;
            Status = LocalizedText.Ref("terminal.status.start_failed", ex.Message);
        }
    }

    private void ReleaseActiveSession(bool kill)
    {
        if (_transportFactory is null)
            return;

        if (_transportFactory.CurrentSessionId is { } id)
            OpenSessions.TryRemove(id, out _);
        _ = kill ? _transportFactory.KillActiveAsync() : _transportFactory.StopActiveAsync();
    }

    private async Task LoadAppearanceAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
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

        var updated = Appearance with { FontFamily = FontFamily, FontSize = FontSize, ColorScheme = ColorScheme };
        Appearance = applyScheme ? TerminalAppearance.ApplyScheme(updated, ColorScheme) : updated;
        _ = SaveAppearanceAsync(Appearance);
    }

    private async Task SaveAppearanceAsync(TerminalSettingsDto settings)
    {
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
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
            Status = title;
    }
}
