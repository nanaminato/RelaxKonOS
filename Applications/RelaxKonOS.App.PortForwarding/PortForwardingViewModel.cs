using RelaxKonOS.AppSDK;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.PortForwarding;

/// <summary>UI state for the device-local SSH forward manager.</summary>
public sealed partial class PortForwardingViewModel : ObservableObject, IDisposable
{
    private readonly IPortForwardingService _service;
    private readonly Func<string, string> _localize;
    private bool _editorOpen;
    private bool _disposed;
    private readonly CancellationTokenSource _windowLifetime = new();
    private Guid? _editorForwardId;
    private PortForwardInfo? _editorForwardBaseline;

    public PortForwardingViewModel(IPortForwardingService service, Func<string, string> localize)
    {
        _service = service;
        _localize = localize;
        var settings = service.GetSettings();
        SshHost = settings.SshHost ?? string.Empty;
        SshUser = settings.SshUser ?? string.Empty;
        SshPortText = settings.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Forwards = new ObservableCollection<PortForwardInfo>();
        RefreshForwards();
        _service.ForwardsChanged += OnForwardsChanged;
    }

    public ObservableCollection<PortForwardInfo> Forwards { get; }
    public Func<string, string> Labels => key => _localize(key);

    [ObservableProperty] private string _targetAddress = "http://localhost:7000";
    [ObservableProperty] private string _preferredLocalPortText = "7000";
    [ObservableProperty] private string _sshHost = string.Empty;
    [ObservableProperty] private string _sshUser = string.Empty;
    [ObservableProperty] private string _sshPortText = "22";
    // Deliberately not part of PortForwardingSettings: passwords are never written to disk.
    [ObservableProperty] private string _sshPassword = string.Empty;
    [ObservableProperty] private PortForwardInfo? _selectedForward;
    private Func<string>? _status;
    public string StatusText => _status?.Invoke() ?? _localize("port_forwarding.status.ready");

    public void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) =>
        OnPropertyChanged(string.Empty);

    private void SetStatus(Func<string> status)
    {
        _status = status;
        OnPropertyChanged(nameof(StatusText));
    }

    private Func<string> Message(string key, params object?[] arguments) => () =>
        arguments.Length == 0 ? _localize(key) : string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize(key), arguments);
    [ObservableProperty] private bool _isBusy;

    public bool HasSelectedForward => SelectedForward is not null;
    public Func<PortForwardInfo?, Task>? ShowForwardEditorAsync { get; set; }
    public Func<Task>? CloseForwardEditorAsync { get; set; }

    [RelayCommand]
    private async Task OpenCreateForwardAsync()
    {
        if (_disposed || IsBusy || _editorOpen) return;
        TargetAddress = "http://localhost:7000";
        PreferredLocalPortText = "7000";
        SelectedForward = null;
        _editorOpen = true;
        _editorForwardId = null;
        try { await (ShowForwardEditorAsync?.Invoke(null) ?? Task.CompletedTask); }
        finally { _editorOpen = false; _editorForwardId = null; }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedForward))]
    private async Task OpenEditForwardAsync(PortForwardInfo? forward)
    {
        if (_disposed || IsBusy || _editorOpen) return;
        forward ??= SelectedForward;
        if (forward is null) return;
        SelectedForward = forward;
        TargetAddress = $"{forward.Scheme}://{forward.RemoteHost}:{forward.RemotePort}{forward.PathAndQuery}";
        PreferredLocalPortText = forward.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _editorOpen = true;
        _editorForwardId = forward.Id;
        _editorForwardBaseline = forward;
        try { await (ShowForwardEditorAsync?.Invoke(forward) ?? Task.CompletedTask); }
        finally { _editorOpen = false; _editorForwardId = null; _editorForwardBaseline = null; }
    }

    [RelayCommand]
    private void SaveConnectionSettings()
    {
        if (_disposed || IsBusy) return;
        SaveConnectionSettingsCore(reportSuccess: true);
    }

    private bool SaveConnectionSettingsCore(bool reportSuccess)
    {
        if (!int.TryParse(SshPortText, out var sshPort) || sshPort is < 1 or > 65535)
        {
            SetStatus(Message("port_forwarding.error.ssh_port_invalid"));
            return false;
        }
        try { _service.SaveSettings(new PortForwardingSettings(SshHost, SshUser, sshPort)); }
        catch (Exception ex) { SetStatus(() => ex.Message); return false; }
        if (reportSuccess)
            SetStatus(Message("port_forwarding.status.settings_saved"));
        return true;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_disposed || IsBusy) return;
        if (_editorOpen && _editorForwardId is not null)
        { SetStatus(Message("port_forwarding.error.target_changed")); return; }
        PortForwardRequest request;
        try { request = ParseRequest(); }
        catch (ArgumentException ex) { SetStatus(() => ex.Message); return; }
        if (!SaveConnectionSettingsCore(reportSuccess: false)) return;
        var succeeded = await RunAsync(async () =>
        {
            var forward = await _service.StartAsync(request, SshPassword);
            if (_disposed) return;
            SelectedForward = forward;
            SetStatus(Message("port_forwarding.status.started", forward.LocalUri));
        });
        if (succeeded)
        {
            SshPassword = string.Empty;
            if (CloseForwardEditorAsync is not null)
                await CloseForwardEditorAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedForward))]
    private async Task UpdateSelectedAsync()
    {
        if (_disposed || IsBusy) return;
        var selectedId = _editorOpen ? _editorForwardId : SelectedForward?.Id;
        if (selectedId is null || !_service.List().Any(forward => forward.Id == selectedId
            && (_editorForwardBaseline is null || (forward.RemoteHost, forward.RemotePort, forward.LocalPort, forward.Scheme, forward.PathAndQuery)
                == (_editorForwardBaseline.RemoteHost, _editorForwardBaseline.RemotePort, _editorForwardBaseline.LocalPort, _editorForwardBaseline.Scheme, _editorForwardBaseline.PathAndQuery))))
        { SetStatus(Message("port_forwarding.error.target_changed")); return; }
        PortForwardRequest request;
        try { request = ParseRequest(); }
        catch (ArgumentException ex) { SetStatus(() => ex.Message); return; }
        if (!SaveConnectionSettingsCore(reportSuccess: false)) return;
        var succeeded = await RunAsync(async () =>
        {
            var forward = await _service.UpdateAsync(selectedId.Value, request, SshPassword);
            if (_disposed) return;
            SelectedForward = forward;
            SetStatus(Message("port_forwarding.status.updated", forward.LocalUri));
        });
        if (succeeded)
        {
            SshPassword = string.Empty;
            if (CloseForwardEditorAsync is not null)
                await CloseForwardEditorAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedForward))]
    private async Task RemoveSelectedAsync()
    {
        if (SelectedForward is null) return;
        var selected = SelectedForward;
        await RunAsync(async () =>
        {
            await _service.RemoveAsync(selected.Id);
            if (_disposed) return;
            SelectedForward = null;
            SetStatus(Message("port_forwarding.status.stopped"));
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedForward))]
    private async Task TestSelectedAsync()
    {
        var forward = SelectedForward;
        if (forward is null) return;
        await RunAsync(async () =>
        {
            using var handler = new HttpClientHandler
            {
                // The probe only reaches the fixed localhost URI emitted by PortForwardInfo.
                // It checks tunnel reachability; it does not change the browser's certificate policy.
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                AllowAutoRedirect = false,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, forward.LocalUri);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, _windowLifetime.Token);
                if (_disposed) return;
                SetStatus(Message("port_forwarding.status.test_succeeded", (int)response.StatusCode, response.ReasonPhrase ?? string.Empty));
            }
            catch (TaskCanceledException)
            {
                throw new InvalidOperationException(_localize("port_forwarding.error.target_timeout"));
            }
            catch (HttpRequestException)
            {
                throw new InvalidOperationException(_localize("port_forwarding.error.target_unreachable"));
            }
        });
    }

    [RelayCommand]
    private void Refresh() => RefreshForwards();

    partial void OnSelectedForwardChanged(PortForwardInfo? value)
    {
        OnPropertyChanged(nameof(HasSelectedForward));
        OpenEditForwardCommand.NotifyCanExecuteChanged();
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        TestSelectedCommand.NotifyCanExecuteChanged();
        if (value is null || _editorOpen) return;
        TargetAddress = $"{value.Scheme}://{value.RemoteHost}:{value.RemotePort}{value.PathAndQuery}";
        PreferredLocalPortText = value.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> RunAsync(Func<Task> operation)
    {
        if (_disposed || IsBusy) return false;
        IsBusy = true;
        try
        {
            await operation();
            return !_disposed;
        }
        catch (Exception ex)
        {
            if (!_disposed) SetStatus(() => ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            RefreshForwards();
        }
    }

    private PortForwardRequest ParseRequest()
    {
        var raw = TargetAddress.Trim();
        Uri.TryCreate(raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw, UriKind.Absolute, out var uri);
        if (uri is null || uri.Port is < 1 or > 65535 || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(_localize("port_forwarding.error.target_invalid"));
        int? preferred = null;
        if (!string.IsNullOrWhiteSpace(PreferredLocalPortText))
        {
            if (!int.TryParse(PreferredLocalPortText, out var parsed) || parsed is < 1 or > 65535)
                throw new ArgumentException(_localize("port_forwarding.error.local_port_invalid"));
            preferred = parsed;
        }
        var request = new PortForwardRequest(uri.Host, uri.Port, uri.Scheme, preferred, uri.PathAndQuery);
        PortForwardRequest.Validate(request, _localize);
        return request;
    }

    private void OnForwardsChanged(object? sender, EventArgs args)
        => Dispatcher.UIThread.Post(RefreshForwards);

    private void RefreshForwards()
    {
        if (_disposed) return;
        IReadOnlyList<PortForwardInfo> current;
        try { current = _service.List(); }
        catch (UnauthorizedAccessException) { current = []; }
        catch (OperationCanceledException) { current = []; }
        Forwards.Clear();
        foreach (var forward in current) Forwards.Add(forward);
        if (SelectedForward is { } selected)
            SelectedForward = current.FirstOrDefault(forward => forward.Id == selected.Id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windowLifetime.Cancel();
        _service.ForwardsChanged -= OnForwardsChanged;
        SshPassword = string.Empty;
    }
}
