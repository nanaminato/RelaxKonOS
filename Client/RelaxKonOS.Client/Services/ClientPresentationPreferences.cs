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
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) };
    private int _listeners;
    private bool _refreshing;
    private long _generation;
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
        session.StateChanged += OnSessionChanged;
        _refresh.Tick += OnRefresh;
    }

    public void Activate() { if (++_listeners == 1) _refresh.Start(); }
    public void Deactivate() { if (_listeners > 0 && --_listeners == 0) _refresh.Stop(); }
    private async void OnRefresh(object? sender, EventArgs args)
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await RefreshAsync(); }
        catch (Exception error) { ReadError = error.Message; }
        finally { _refreshing = false; }
    }
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnSessionChanged(sender, args)); return; }
        ++_generation;
        ReadError = null;
        Registry = RegistryAppearanceSettings.Default;
        TerminalPadding = TerminalPaddingSettings.Default;
        Terminal = TerminalSettingsDto.Default;
        Changed?.Invoke(this, EventArgs.Empty);
        if (_session.State == AuthSessionState.Authenticated && _listeners > 0) { _refresh.Start(); OnRefresh(null, EventArgs.Empty); }
        else _refresh.Stop();
    }

    public async Task RefreshAsync()
    {
        if (_session.State != AuthSessionState.Authenticated || _session.CurrentUser is not { } user) return;
        var generation = ++_generation;
        var origin = _session.EffectiveBaseUrl;
        var device = _session.CurrentDevice?.Id;
        var workspace = _session.CurrentWorkspace?.Id;
        var client = _client.ForUser(user.Id);
        var registry = device is not null ? ReadAsync<RegistryAppearanceSettings>(client, RegistryScope.Device, ClientPresentationPaths.Registry) : Task.FromResult(RegistryAppearanceSettings.Default);
        var padding = device is not null ? ReadAsync<TerminalPaddingSettings>(client, RegistryScope.Device, ClientPresentationPaths.Terminal) : Task.FromResult(TerminalPaddingSettings.Default);
        var terminal = workspace is not null ? ReadAsync<TerminalSettingsDto>(client, RegistryScope.Workspace, "Workspace\\Terminal") : Task.FromResult(TerminalSettingsDto.Default);
        try { await Task.WhenAll(registry, padding, terminal); }
        catch (Exception error) { if (generation == _generation) ReadError = error.Message; throw; }
        if (generation != _generation || _session.CurrentUser?.Id != user.Id || _session.CurrentDevice?.Id != device
            || _session.CurrentWorkspace?.Id != workspace || _session.EffectiveBaseUrl != origin) return;
        if (!registry.Result.IsValid || !padding.Result.IsValid) throw new InvalidOperationException("Invalid client presentation settings.");
        ReadError = null;
        if (Registry == registry.Result && TerminalPadding == padding.Result && Terminal == terminal.Result) return;
        Registry = registry.Result; TerminalPadding = padding.Result; Terminal = terminal.Result;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<T> ReadAsync<T>(IRegistryClient client, RegistryScope scope, string path)
    {
        var values = await client.ListValuesAsync(scope, path);
        if (values.Count == 0) throw new InvalidOperationException($"No presentation settings at {path}.");
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
            if (entry.Path == ClientPresentationPaths.Registry) Registry = Patch(Registry, entry);
            else if (entry.Path == ClientPresentationPaths.Terminal) TerminalPadding = Patch(TerminalPadding, entry);
            else return;
        }
        else if (entry.Scope == RegistryScope.Workspace && entry.Address.ScopeId == _session.CurrentWorkspace?.Id && entry.Path == "Workspace\\Terminal")
            Terminal = Patch(Terminal, entry);
        else return;
        ++_generation;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnTerminalSaved(string origin, Guid workspace, TerminalSettingsDto settings)
    {
        if (_session.State != AuthSessionState.Authenticated || !SameOrigin(origin, _session.EffectiveBaseUrl) || workspace != _session.CurrentWorkspace?.Id) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnTerminalSaved(origin, workspace, settings)); return; }
        ++_generation; Terminal = settings;
        Changed?.Invoke(this, EventArgs.Empty);
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
        _changes.RegistrySaved -= OnRegistrySaved; _changes.TerminalSaved -= OnTerminalSaved;
        _session.StateChanged -= OnSessionChanged;
    }
}
