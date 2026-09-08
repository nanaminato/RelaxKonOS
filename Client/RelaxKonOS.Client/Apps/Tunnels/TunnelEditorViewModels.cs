using System.Collections.ObjectModel;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Client.Apps.Tunnels;

public sealed partial class TunnelProfileEditorViewModel : ObservableObject
{
    private readonly IRemoteTunnelClient _client;
    private readonly TunnelServerProfileDto? _original;
    public IReadOnlyList<TunnelAuthKind> AuthKinds { get; } = Enum.GetValues<TunnelAuthKind>();
    public IReadOnlyList<TunnelTlsMode> TlsModes { get; } = Enum.GetValues<TunnelTlsMode>();
    public IReadOnlyList<TunnelRuntimeMode> RuntimeModes { get; } = Enum.GetValues<TunnelRuntimeMode>();

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _host;
    [ObservableProperty] private int _port;
    [ObservableProperty] private TunnelAuthKind _authKind;
    [ObservableProperty] private TunnelTlsMode _tlsMode;
    [ObservableProperty] private TunnelRuntimeMode _runtimeMode;
    [ObservableProperty] private string _externalPath;
    [ObservableProperty] private string _token = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;

    public Func<Task>? CloseAsync { get; set; }
    public Func<Task>? SavedAsync { get; set; }
    public Func<Task<bool>>? RequestDeletionConfirmationAsync { get; set; }
    public bool IsEditing => _original is not null;
    public bool IsManagedRuntime => RuntimeMode == TunnelRuntimeMode.Managed;
    public bool IsTokenAuth => AuthKind == TunnelAuthKind.Token;
    public bool CanDelete => IsEditing && !IsBusy;

    public TunnelProfileEditorViewModel(IRemoteTunnelClient client, TunnelServerProfileDto? original, string? token = null)
    {
        _client = client; _original = original;
        _name = original?.Name ?? string.Empty; _host = original?.Host ?? string.Empty; _port = original?.Port ?? 7000;
        _authKind = original?.AuthKind ?? TunnelAuthKind.Token; _tlsMode = original?.TlsMode ?? TunnelTlsMode.Default;
        _runtimeMode = original?.RuntimeMode ?? TunnelRuntimeMode.Managed; _externalPath = original?.ExternalExecutablePath ?? string.Empty;
        _token = token ?? string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var request = new UpsertTunnelServerProfileRequest(Name, Host, Port, AuthKind, TlsMode, RuntimeMode,
                RuntimeMode == TunnelRuntimeMode.External ? ExternalPath : null, _original?.Revision);
            var saved = _original is null ? await _client.CreateProfileAsync(request) : await _client.UpdateProfileAsync(_original.Id, request);
            if (AuthKind == TunnelAuthKind.Token && !string.IsNullOrWhiteSpace(Token)) await _client.SetProfileTokenAsync(saved.Id, Token);
            Token = string.Empty; StatusText = LocalizedText.Get("tunnels.status.profile_saved");
            if (SavedAsync is not null) await SavedAsync();
            if (CloseAsync is not null) await CloseAsync();
        }
        catch (Exception ex) { StatusText = TunnelProblemText.Format(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!CanDelete || _original is null || RequestDeletionConfirmationAsync is null || !await RequestDeletionConfirmationAsync()) return;
        IsBusy = true;
        try
        {
            await _client.DeleteProfileAsync(_original.Id); StatusText = LocalizedText.Get("tunnels.status.profile_deleted");
            if (SavedAsync is not null) await SavedAsync();
            if (CloseAsync is not null) await CloseAsync();
        }
        catch (Exception ex) { StatusText = TunnelProblemText.Format(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ProbeExternalRuntimeAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(ExternalPath)) return;
        IsBusy = true;
        try
        {
            var probe = await _client.DetectExternalRuntimeAsync(ExternalPath);
            StatusText = probe.State == TunnelRuntimeState.Available ? LocalizedText.Format("tunnels.status.external_available", probe.Version ?? "—") : TunnelProblemText.Format(probe.ProblemCode);
        }
        catch (Exception ex) { StatusText = TunnelProblemText.Format(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand] private Task Close() => CloseAsync?.Invoke() ?? Task.CompletedTask;
    partial void OnRuntimeModeChanged(TunnelRuntimeMode value) => OnPropertyChanged(nameof(IsManagedRuntime));
    partial void OnAuthKindChanged(TunnelAuthKind value) => OnPropertyChanged(nameof(IsTokenAuth));
    partial void OnIsBusyChanged(bool value) => DeleteCommand.NotifyCanExecuteChanged();
}

public sealed partial class TunnelDefinitionEditorViewModel : ObservableObject
{
    private readonly IRemoteTunnelClient _client;
    private readonly TunnelDefinitionDto? _original;
    public ObservableCollection<TunnelServerProfileDto> Profiles { get; }
    public IReadOnlyList<TunnelProtocol> Protocols { get; } = Enum.GetValues<TunnelProtocol>();

    [ObservableProperty] private string _name;
    [ObservableProperty] private TunnelServerProfileDto? _profile;
    [ObservableProperty] private TunnelProtocol _protocol;
    [ObservableProperty] private string _localHost;
    [ObservableProperty] private int _localPort;
    [ObservableProperty] private int? _remotePort;
    [ObservableProperty] private string _domain;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _encryption;
    [ObservableProperty] private bool _compression;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;

    public Func<Task>? CloseAsync { get; set; }
    public Func<Task>? SavedAsync { get; set; }
    public Func<Task<bool>>? RequestDeletionConfirmationAsync { get; set; }
    public bool IsEditing => _original is not null;
    public bool CanDelete => IsEditing && !IsBusy;
    public bool UsesRemotePort => Protocol is TunnelProtocol.Tcp or TunnelProtocol.Udp;
    public bool UsesDomain => Protocol is TunnelProtocol.Http or TunnelProtocol.Https;

    public TunnelDefinitionEditorViewModel(IRemoteTunnelClient client, IEnumerable<TunnelServerProfileDto> profiles, TunnelDefinitionDto? original)
    {
        _client = client; _original = original; Profiles = new ObservableCollection<TunnelServerProfileDto>(profiles);
        _name = original?.Name ?? string.Empty; _profile = Profiles.FirstOrDefault(x => x.Id == original?.ServerProfileId) ?? Profiles.FirstOrDefault();
        _protocol = original?.Protocol ?? TunnelProtocol.Tcp; _localHost = original?.LocalHost ?? "127.0.0.1"; _localPort = original?.LocalPort ?? 8080;
        _remotePort = original?.RemotePort ?? 8080; _domain = original?.Domain ?? string.Empty; _enabled = original?.Enabled ?? true;
        _encryption = original?.Encryption ?? false; _compression = original?.Compression ?? false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        if (Profile is null) { StatusText = TunnelProblemText.Format("tunnel.profile_not_found"); return; }
        IsBusy = true;
        try
        {
            var request = new UpsertTunnelDefinitionRequest(Profile.Id, Name, Protocol, LocalHost, LocalPort,
                UsesRemotePort ? RemotePort : null, UsesDomain && !string.IsNullOrWhiteSpace(Domain) ? Domain : null,
                Enabled, Encryption, Compression, _original?.Revision);
            _ = _original is null ? await _client.CreateTunnelAsync(request) : await _client.UpdateTunnelAsync(_original.Id, request);
            StatusText = LocalizedText.Get("tunnels.status.tunnel_saved");
            if (SavedAsync is not null) await SavedAsync();
            if (CloseAsync is not null) await CloseAsync();
        }
        catch (Exception ex) { StatusText = TunnelProblemText.Format(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!CanDelete || _original is null || RequestDeletionConfirmationAsync is null || !await RequestDeletionConfirmationAsync()) return;
        IsBusy = true;
        try
        {
            await _client.DeleteTunnelAsync(_original.Id); StatusText = LocalizedText.Get("tunnels.status.tunnel_deleted");
            if (SavedAsync is not null) await SavedAsync();
            if (CloseAsync is not null) await CloseAsync();
        }
        catch (Exception ex) { StatusText = TunnelProblemText.Format(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand] private Task Close() => CloseAsync?.Invoke() ?? Task.CompletedTask;
    partial void OnIsBusyChanged(bool value) => DeleteCommand.NotifyCanExecuteChanged();
    partial void OnProtocolChanged(TunnelProtocol value)
    {
        OnPropertyChanged(nameof(UsesRemotePort));
        OnPropertyChanged(nameof(UsesDomain));
    }
}

public sealed partial class TunnelLogViewModel(IRemoteTunnelClient client, TunnelServerProfileDto profile) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    [ObservableProperty] private string _logsText = string.Empty;
    [ObservableProperty] private string _statusText = LocalizedText.Get("tunnels.status.loading");
    [ObservableProperty] private bool _isBusy;
    public string ProfileName => profile.Name;

    public async Task StartAsync()
    {
        await RefreshAsync();
        while (!_lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token); await RefreshAsync(); }
            catch (OperationCanceledException) { }
        }
    }
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            LogsText = string.Join(Environment.NewLine, (await client.GetLogsAsync(profile.Id, _lifetime.Token)).Select(log => $"{log.Timestamp:HH:mm:ss} {log.Level}: {log.Message}"));
            StatusText = LocalizedText.Get("tunnels.logs_updated");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = TunnelProblemText.Format(ex); }
        finally { IsBusy = false; }
    }
    public void Dispose() => _lifetime.Cancel();
}

internal static class TunnelProblemText
{
    public static string Format(Exception exception) => exception is TunnelRequestException request
        ? Format(request.ProblemCode)
        : LocalizedText.Get("tunnels.status.failed");

    public static string Format(string? problemCode)
    {
        if (string.IsNullOrWhiteSpace(problemCode)) return LocalizedText.Get("tunnels.status.failed");
        var key = $"tunnels.problem.{problemCode}";
        var text = LocalizedText.Get(key);
        return text == key ? LocalizedText.Get("tunnels.status.failed") : text;
    }
}
