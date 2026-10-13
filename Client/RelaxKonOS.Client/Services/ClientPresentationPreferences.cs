using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Registry;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services;

public sealed class PresentationSettingsChanges
{
    public event Action<RegistryEntryDto>? RegistrySaved;
    public event Action<string, Guid, TerminalSettingsDto>? TerminalSaved;
    public event Action<string, RelaxKonOS.Protocol.Hubs.RegistrySettingsChanged>? RegistryInvalidated;
    public event Action<string, Guid>? RecoveryRequested;
    public void Invalidate(string origin, RelaxKonOS.Protocol.Hubs.RegistrySettingsChanged change) => RegistryInvalidated?.Invoke(origin, change);
    public void RequestRecovery(string origin, Guid workspace) => RecoveryRequested?.Invoke(origin, workspace);
    public void Publish(RegistryEntryDto entry) => RegistrySaved?.Invoke(entry);
    public void Publish(string origin, Guid workspace, TerminalSettingsDto settings) => TerminalSaved?.Invoke(origin, workspace, settings);
}

/// <summary>Current authenticated client's presentation settings. Device geometry never follows another device.</summary>
public sealed class ClientPresentationPreferences : IDisposable
{
    private readonly IRegistryClient _client;
    private readonly IAuthSession _session;
    private readonly PresentationSettingsChanges _changes;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(300) };
    private int _listeners;
    private int _policyListeners;
    private CancellationTokenSource _readRetry = new();
    private int _retrySeconds = 1;
    private bool _retryScheduled;
    private long _retryGeneration;
    private bool _refreshing;
    private long _generation;
    private string? _loadedIdentity;
    private bool _stale;
    private Task _initialRead = Task.CompletedTask;
    private string? _initialIdentity;
    private readonly Dictionary<(RegistryScope, Guid, string), long> _revisions = [];
    private readonly HashSet<string> _invalidated = [];
    private bool _readingInvalidations;
    public ClientSynchronizationSettings Synchronization { get; private set; } = ClientSynchronizationSettings.Default;
    public bool PollingActive => _refresh.IsEnabled;
    public RegistryAppearanceSettings Registry { get; private set; } = RegistryAppearanceSettings.Default;
    public TerminalPaddingSettings TerminalPadding { get; private set; } = TerminalPaddingSettings.Default;
    public TerminalSettingsDto Terminal { get; private set; } = TerminalSettingsDto.Default;
    public string? ReadError { get; private set; }
    public event EventHandler? Changed;

    public ClientPresentationPreferences(IRegistryClient client, IAuthSession session, PresentationSettingsChanges changes)
    {
        _client = client; _session = session; _changes = changes;
        changes.RegistrySaved += OnRegistrySaved;
        changes.TerminalSaved += OnTerminalSaved;
        changes.RegistryInvalidated += OnInvalidated;
        session.StateChanged += OnSessionChanged;
        _refresh.Tick += OnRefresh;
    }

    private string Identity => $"{_session.EffectiveBaseUrl}\n{_session.CurrentUser?.Id}\n{_session.CurrentDevice?.Id}\n{_session.CurrentWorkspace?.Id}";
    public Task EnsureLoadedAsync()
    {
        if (_loadedIdentity == Identity && !_stale && Synchronization.Mode != "manual") return Task.CompletedTask;
        if (_initialIdentity == Identity && !_initialRead.IsCompleted) return _initialRead;
        _initialIdentity = Identity;
        return _initialRead = RefreshAsync();
    }
    private void ConfigurePolling()
    {
        _refresh.Stop();
        if (Synchronization.Mode == "manual") { CancelReadRetry(); _invalidated.Clear(); }
        if (_listeners + _policyListeners > 0 && _session.State == AuthSessionState.Authenticated && Synchronization.Mode == "polling")
        { _refresh.Interval = TimeSpan.FromSeconds(Synchronization.PollIntervalSeconds); _refresh.Start(); }
    }
    public void Activate() { ++_listeners; ConfigurePolling(); }
    public void Deactivate() { if (_listeners > 0) --_listeners; ConfigurePolling(); if (_listeners + _policyListeners == 0) CancelReadRetry(); }
    public void ActivatePolicy() { ++_policyListeners; ConfigurePolling(); }
    public void DeactivatePolicy() { if (_policyListeners > 0) --_policyListeners; ConfigurePolling(); if (_listeners + _policyListeners == 0) CancelReadRetry(); }
    private void CancelReadRetry()
    {
        ++_retryGeneration; _retryScheduled = false;
        _readRetry.Cancel(); _readRetry.Dispose(); _readRetry = new();
    }
    private async void OnRefresh(object? sender, EventArgs args)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await RefreshAsync();
            if (_session.CurrentWorkspace is { } workspace && _session.EffectiveBaseUrl is { } origin)
                _changes.RequestRecovery(new Uri(origin).GetLeftPart(UriPartial.Authority), workspace.Id);
        }
        catch (Exception error) { ReadError = error.Message; }
        finally { _refreshing = false; }
    }
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnSessionChanged(sender, args)); return; }
        ++_generation;
        ReadError = null;
        CancelReadRetry();
        _loadedIdentity = null; _stale = false;
        _invalidated.Clear(); _revisions.Clear();
        Synchronization = ClientSynchronizationSettings.Default;
        Registry = RegistryAppearanceSettings.Default;
        TerminalPadding = TerminalPaddingSettings.Default;
        Terminal = TerminalSettingsDto.Default;
        Changed?.Invoke(this, EventArgs.Empty);
        ConfigurePolling();
        if (_session.State == AuthSessionState.Authenticated && _listeners + _policyListeners > 0) OnRefresh(null, EventArgs.Empty);
    }

    public async Task RefreshAsync()
    {
        if (_session.State != AuthSessionState.Authenticated || _session.CurrentUser is not { } user) return;
        var generation = ++_generation;
        var origin = _session.EffectiveBaseUrl;
        var device = _session.CurrentDevice?.Id;
        var workspace = _session.CurrentWorkspace?.Id;
        var client = _client.ForUser(user.Id);
        long registryRevision = 0, paddingRevision = 0, terminalRevision = 0, synchronizationRevision = 0;
        var synchronization = device is not null ? ReadAsync<ClientSynchronizationSettings>(client, RegistryScope.Device, ClientPresentationPaths.Synchronization, revision => synchronizationRevision = revision) : Task.FromResult(ClientSynchronizationSettings.Default);
        var registry = device is not null ? ReadAsync<RegistryAppearanceSettings>(client, RegistryScope.Device, ClientPresentationPaths.Registry, revision => registryRevision = revision) : Task.FromResult(RegistryAppearanceSettings.Default);
        var padding = device is not null ? ReadAsync<TerminalPaddingSettings>(client, RegistryScope.Device, ClientPresentationPaths.Terminal, revision => paddingRevision = revision) : Task.FromResult(TerminalPaddingSettings.Default);
        var terminal = workspace is not null ? ReadAsync<TerminalSettingsDto>(client, RegistryScope.Workspace, "Workspace\\Terminal", revision => terminalRevision = revision) : Task.FromResult(TerminalSettingsDto.Default);
        try { await Task.WhenAll(registry, padding, terminal, synchronization); }
        catch (Exception error) { if (generation == _generation) ReadError = error.Message; throw; }
        if (generation != _generation || _session.CurrentUser?.Id != user.Id || _session.CurrentDevice?.Id != device
            || _session.CurrentWorkspace?.Id != workspace || _session.EffectiveBaseUrl != origin) return;
        if (!registry.Result.IsValid || !padding.Result.IsValid || !synchronization.Result.IsValid) throw new InvalidOperationException("Invalid client presentation settings.");
        ReadError = null;
        _loadedIdentity = Identity; _stale = false;
        if (device is { } deviceId)
        {
            _revisions[(RegistryScope.Device, deviceId, ClientPresentationPaths.Registry)] = registryRevision;
            _revisions[(RegistryScope.Device, deviceId, ClientPresentationPaths.Terminal)] = paddingRevision;
            _revisions[(RegistryScope.Device, deviceId, ClientPresentationPaths.Synchronization)] = synchronizationRevision;
        }
        if (workspace is { } workspaceId) _revisions[(RegistryScope.Workspace, workspaceId, "Workspace\\Terminal")] = terminalRevision;
        var unchanged = Registry == registry.Result && TerminalPadding == padding.Result && Terminal == terminal.Result && Synchronization == synchronization.Result;
        Synchronization = synchronization.Result;
        ConfigurePolling();
        if (unchanged) return;
        Registry = registry.Result; TerminalPadding = padding.Result; Terminal = terminal.Result;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<T> ReadAsync<T>(IRegistryClient client, RegistryScope scope, string path, Action<long>? revision = null)
    {
        var values = await client.ListValuesAsync(scope, path);
        if (values.Count == 0) throw new InvalidOperationException($"No presentation settings at {path}.");
        revision?.Invoke(values[0].Revision);
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(values.ToDictionary(entry => entry.Name, entry => entry.DesiredValue)), RelaxKonOSJsonOptions.Default)
            ?? throw new InvalidOperationException("Missing presentation settings.");
    }
    private static T Patch<T>(T baseline, RegistryEntryDto field)
    {
        var json = JsonSerializer.SerializeToNode(baseline, RelaxKonOSJsonOptions.Default)!;
        json[field.Name] = JsonNode.Parse(field.DesiredValue.GetRawText());
        return json.Deserialize<T>(RelaxKonOSJsonOptions.Default)!;
    }
    private bool IsCurrent(RegistryAddress address) => _session.State == AuthSessionState.Authenticated
        && address.OwnerUserId == _session.CurrentUser?.Id && SameOrigin(address.ServerId, _session.EffectiveBaseUrl);
    private static bool SameOrigin(string origin, string? url) => Uri.TryCreate(url, UriKind.Absolute, out var current)
        && string.Equals(origin.TrimEnd('/'), current.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private void OnRegistrySaved(RegistryEntryDto entry)
    {
        if (!IsCurrent(entry.Address)) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnRegistrySaved(entry)); return; }
        if (entry.Scope == RegistryScope.Device && entry.Address.ScopeId == _session.CurrentDevice?.Id)
        {
            if (entry.Path == ClientPresentationPaths.Synchronization) { Synchronization = Patch(Synchronization, entry); ConfigurePolling(); }
            else if (entry.Path == ClientPresentationPaths.Registry) Registry = Patch(Registry, entry);
            else if (entry.Path == ClientPresentationPaths.Terminal) TerminalPadding = Patch(TerminalPadding, entry);
            else return;
        }
        else if (entry.Scope == RegistryScope.Workspace && entry.Address.ScopeId == _session.CurrentWorkspace?.Id && entry.Path == "Workspace\\Terminal")
            Terminal = Patch(Terminal, entry);
        else return;
        ++_generation;
        var key = (entry.Scope, entry.Address.ScopeId, entry.Path);
        if (_revisions.TryGetValue(key, out var previous) && entry.Revision > previous + 1)
        {
            _stale = true;
            _invalidated.Add(entry.Path);
            if (Synchronization.Mode != "manual") _ = ReadInvalidationsAsync();
        }
        else _revisions[key] = entry.Revision;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnTerminalSaved(string origin, Guid workspace, TerminalSettingsDto settings)
    {
        if (_session.State != AuthSessionState.Authenticated || !SameOrigin(origin, _session.EffectiveBaseUrl) || workspace != _session.CurrentWorkspace?.Id) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnTerminalSaved(origin, workspace, settings)); return; }
        ++_generation; Terminal = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnInvalidated(string origin, RelaxKonOS.Protocol.Hubs.RegistrySettingsChanged change)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnInvalidated(origin, change)); return; }
        if (_session.State != AuthSessionState.Authenticated || change.UserId != _session.CurrentUser?.Id || !SameOrigin(origin, _session.EffectiveBaseUrl)
            || Synchronization.Mode == "manual" || change.Scope == RegistryScope.Device && change.ScopeId != _session.CurrentDevice?.Id
            || change.Scope == RegistryScope.Workspace && change.ScopeId != _session.CurrentWorkspace?.Id) return;
        if (change.Path is not (ClientPresentationPaths.Registry or ClientPresentationPaths.Terminal or ClientPresentationPaths.Synchronization or "Workspace\\Terminal")) return;
        if (_revisions.GetValueOrDefault((change.Scope, change.ScopeId, change.Path)) >= change.Revision) return;
        if (_listeners == 0 && (change.Path != ClientPresentationPaths.Synchronization || _policyListeners == 0)) { _stale = true; return; }
        _invalidated.Add(change.Path);
        _ = ReadInvalidationsAsync();
    }
    private async Task ReadInvalidationsAsync()
    {
        if (_readingInvalidations || _retryScheduled || Synchronization.Mode == "manual" || _listeners + _policyListeners == 0) return;
        _readingInvalidations = true;
        string? readingPath = null;
        var identity = Identity;
        try
        {
            await Task.Yield();
            while (_invalidated.Count > 0 && Synchronization.Mode != "manual")
            {
                readingPath = _invalidated.First(); _invalidated.Remove(readingPath);
                var generation = _generation; identity = Identity;
                var owner = _session.CurrentUser?.Id;
                if (owner is null) break;
                var scope = readingPath.StartsWith("Device", StringComparison.Ordinal) ? RegistryScope.Device : RegistryScope.Workspace;
                var scopeId = scope == RegistryScope.Device ? _session.CurrentDevice?.Id : _session.CurrentWorkspace?.Id;
                var fields = await _client.ForUser(owner.Value).ListValuesAsync(scope, readingPath);
                if (identity != Identity) continue;
                if (generation != _generation) { _stale = true; _invalidated.Add(readingPath); continue; }
                if (Synchronization.Mode == "manual") break;
                if (fields.Count == 0) throw new InvalidOperationException("Missing notified presentation settings.");
                var json = JsonSerializer.Serialize(fields.ToDictionary(field => field.Name, field => field.DesiredValue));
                switch (readingPath)
                {
                    case ClientPresentationPaths.Registry:
                        var registry = JsonSerializer.Deserialize<RegistryAppearanceSettings>(json, RelaxKonOSJsonOptions.Default)!;
                        if (!registry.IsValid) throw new InvalidOperationException("Invalid registry appearance.");
                        Registry = registry; break;
                    case ClientPresentationPaths.Terminal:
                        var padding = JsonSerializer.Deserialize<TerminalPaddingSettings>(json, RelaxKonOSJsonOptions.Default)!;
                        if (!padding.IsValid) throw new InvalidOperationException("Invalid terminal padding.");
                        TerminalPadding = padding; break;
                    case ClientPresentationPaths.Synchronization:
                        var synchronization = JsonSerializer.Deserialize<ClientSynchronizationSettings>(json, RelaxKonOSJsonOptions.Default)!;
                        if (!synchronization.IsValid) throw new InvalidOperationException("Invalid synchronization policy.");
                        Synchronization = synchronization; ConfigurePolling(); break;
                    default: Terminal = JsonSerializer.Deserialize<TerminalSettingsDto>(json, RelaxKonOSJsonOptions.Default)!; break;
                }
                if (scopeId is { } id) _revisions[(scope, id, readingPath)] = fields[0].Revision;
                ReadError = null; _retrySeconds = 1;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception error)
        {
            if (identity == Identity) { ReadError = error.Message; _stale = true; if (readingPath is not null) _invalidated.Add(readingPath); ScheduleReadRetry(); }
        }
        finally { _readingInvalidations = false; }
    }
    private async void ScheduleReadRetry()
    {
        if (_retryScheduled || Synchronization.Mode == "manual" || _listeners + _policyListeners == 0) return;
        _retryScheduled = true;
        var generation = _retryGeneration; var token = _readRetry.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_retrySeconds), token);
            _retrySeconds = Math.Min(60, _retrySeconds * 2);
            if (generation != _retryGeneration) return;
            _retryScheduled = false;
            await ReadInvalidationsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task SaveSidebarWidthAsync(int width)
    {
        var user = _session.CurrentUser?.Id;
        var device = _session.CurrentDevice?.Id;
        var origin = _session.EffectiveBaseUrl;
        if (user is null || device is null) return;
        await _writes.WaitAsync();
        try
        {
            if (_session.CurrentUser?.Id != user || _session.CurrentDevice?.Id != device || _session.EffectiveBaseUrl != origin) return;
            var client = _client.ForUser(user.Value);
            var fields = await client.ListValuesAsync(RegistryScope.Device, ClientPresentationPaths.Registry);
            var field = fields.Single(value => value.Name == "sidebarWidth");
            await client.SaveAsync(new(RegistryScope.Device, field.Path, field.Name, field.ValueType,
                JsonSerializer.SerializeToElement(Math.Clamp(width, 180, 600)), field.Revision));
        }
        finally { _writes.Release(); }
    }
    public void Dispose()
    {
        _refresh.Stop(); _refresh.Tick -= OnRefresh;
        _readRetry.Cancel(); _readRetry.Dispose();
        _changes.RegistryInvalidated -= OnInvalidated;
        _changes.RegistrySaved -= OnRegistrySaved; _changes.TerminalSaved -= OnTerminalSaved;
        _session.StateChanged -= OnSessionChanged;
    }
}
