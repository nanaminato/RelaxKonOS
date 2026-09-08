using System.Collections.ObjectModel;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.PortForwarding.ViewModels;

/// <summary>UI state for the device-local SSH forward manager.</summary>
public sealed partial class PortForwardingViewModel : ObservableObject, IDisposable
{
    private readonly IPortForwardingService _service;

    public PortForwardingViewModel(IPortForwardingService service)
    {
        _service = service;
        var settings = service.GetSettings();
        SshHost = settings.SshHost ?? string.Empty;
        SshUser = settings.SshUser ?? string.Empty;
        SshPortText = settings.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Forwards = new ObservableCollection<PortForwardInfo>();
        RefreshForwards();
        _service.ForwardsChanged += OnForwardsChanged;
    }

    public ObservableCollection<PortForwardInfo> Forwards { get; }

    [ObservableProperty] private string _targetAddress = "http://localhost:7000";
    [ObservableProperty] private string _preferredLocalPortText = "7000";
    [ObservableProperty] private string _sshHost = string.Empty;
    [ObservableProperty] private string _sshUser = string.Empty;
    [ObservableProperty] private string _sshPortText = "22";
    // Deliberately not part of PortForwardingSettings: passwords are never written to disk.
    [ObservableProperty] private string _sshPassword = string.Empty;
    [ObservableProperty] private PortForwardInfo? _selectedForward;
    [ObservableProperty] private string _statusText = LocalizedText.Get("port_forwarding.status.ready");
    [ObservableProperty] private bool _isBusy;

    public bool HasSelectedForward => SelectedForward is not null;
    public Func<PortForwardInfo?, Task>? ShowForwardEditorAsync { get; set; }
    public Func<Task>? CloseForwardEditorAsync { get; set; }

    [RelayCommand]
    private Task OpenCreateForwardAsync()
    {
        TargetAddress = "http://localhost:7000";
        PreferredLocalPortText = "7000";
        SelectedForward = null;
        return ShowForwardEditorAsync?.Invoke(null) ?? Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedForward))]
    private async Task OpenEditForwardAsync(PortForwardInfo? forward)
    {
        forward ??= SelectedForward;
        if (forward is null) return;
        SelectedForward = forward;
        TargetAddress = $"{forward.Scheme}://{forward.RemoteHost}:{forward.RemotePort}{forward.PathAndQuery}";
        PreferredLocalPortText = forward.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await (ShowForwardEditorAsync?.Invoke(forward) ?? Task.CompletedTask);
    }

    [RelayCommand]
    private void SaveConnectionSettings()
    {
        SaveConnectionSettingsCore(reportSuccess: true);
    }

    private bool SaveConnectionSettingsCore(bool reportSuccess)
    {
        if (!int.TryParse(SshPortText, out var sshPort) || sshPort is < 1 or > 65535)
        {
            StatusText = LocalizedText.Get("port_forwarding.error.ssh_port_invalid");
            return false;
        }
        _service.SaveSettings(new PortForwardingSettings(SshHost, SshUser, sshPort));
        if (reportSuccess)
            StatusText = LocalizedText.Get("port_forwarding.status.settings_saved");
        return true;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!SaveConnectionSettingsCore(reportSuccess: false)) return;
        var succeeded = await RunAsync(async () =>
        {
            var forward = await _service.StartAsync(ParseRequest(), SshPassword);
            SelectedForward = forward;
            StatusText = LocalizedText.Format("port_forwarding.status.started", forward.LocalUri);
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
        if (SelectedForward is null) return;
        if (!SaveConnectionSettingsCore(reportSuccess: false)) return;
        var succeeded = await RunAsync(async () =>
        {
            var forward = await _service.UpdateAsync(SelectedForward.Id, ParseRequest(), SshPassword);
            SelectedForward = forward;
            StatusText = LocalizedText.Format("port_forwarding.status.updated", forward.LocalUri);
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
            SelectedForward = null;
            StatusText = LocalizedText.Get("port_forwarding.status.stopped");
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
                    request, HttpCompletionOption.ResponseHeadersRead);
                StatusText = LocalizedText.Format("port_forwarding.status.test_succeeded", (int)response.StatusCode, response.ReasonPhrase ?? string.Empty);
            }
            catch (TaskCanceledException)
            {
                throw new InvalidOperationException(LocalizedText.Get("port_forwarding.error.target_timeout"));
            }
            catch (HttpRequestException)
            {
                throw new InvalidOperationException(LocalizedText.Get("port_forwarding.error.target_unreachable"));
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
        if (value is null) return;
        TargetAddress = $"{value.Scheme}://{value.RemoteHost}:{value.RemotePort}{value.PathAndQuery}";
        PreferredLocalPortText = value.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> RunAsync(Func<Task> operation)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            await operation();
            return true;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            Uri.TryCreate("http://" + raw, UriKind.Absolute, out uri);
        if (uri is null || uri.Port is < 1 or > 65535)
            throw new ArgumentException(LocalizedText.Get("port_forwarding.error.target_invalid"));
        int? preferred = null;
        if (!string.IsNullOrWhiteSpace(PreferredLocalPortText))
        {
            if (!int.TryParse(PreferredLocalPortText, out var parsed) || parsed is < 1 or > 65535)
                throw new ArgumentException(LocalizedText.Get("port_forwarding.error.local_port_invalid"));
            preferred = parsed;
        }
        return new PortForwardRequest(uri.Host, uri.Port, uri.Scheme, preferred, uri.PathAndQuery);
    }

    private void OnForwardsChanged(object? sender, EventArgs args)
        => Dispatcher.UIThread.Post(RefreshForwards);

    private void RefreshForwards()
    {
        var current = _service.List();
        Forwards.Clear();
        foreach (var forward in current) Forwards.Add(forward);
        if (SelectedForward is { } selected)
            SelectedForward = current.FirstOrDefault(forward => forward.Id == selected.Id);
    }

    public void Dispose() => _service.ForwardsChanged -= OnForwardsChanged;
}
