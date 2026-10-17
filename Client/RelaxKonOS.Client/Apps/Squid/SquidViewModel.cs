using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Client.Apps.Squid;

public partial class SquidViewModel(ISquidClient client) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private bool _disposed;
    private string? _pendingRoute;
    private object? _pendingBody;
    private string? _pendingKey;
    private Guid? _operation;
    [ObservableProperty] private string _bindAddress = "127.0.0.1";
    [ObservableProperty] private string _port = "3128";
    [ObservableProperty] private bool _allowRemoteClients;
    [ObservableProperty] private string _allowedNetworks = "127.0.0.1/32\n::1/128";
    [ObservableProperty] private string _allowedDomains = "";
    [ObservableProperty] private string _deniedDomains = "";
    [ObservableProperty] private string _upstreamHost = "";
    [ObservableProperty] private string _upstreamPort = "7890";
    [ObservableProperty] private bool _adoptExistingConfiguration;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canRead;
    [ObservableProperty] private bool _canManage;
    [ObservableProperty] private SquidConfigurationSnapshot? _snapshot;
    public Func<Task<bool>>? ConfirmApplyAsync { get; set; }
    public Func<Task<bool>>? ConfirmReloadAsync { get; set; }
    public bool HasUnsavedChanges
    {
        get
        {
            if (!_initialized) return false;
            try { return SquidConfigurationRenderer.Preview(BuildConfiguration()).Configuration != SquidConfigurationRenderer.Preview(Snapshot?.State.Configuration ?? new SquidConfiguration()).Configuration; }
            catch (InvalidOperationException) { return true; }
        }
    }
    public bool HasPending => _pendingKey is not null || _operation is not null;
    public bool CanEdit => !_disposed && !_lifetime.IsCancellationRequested && CanRead && CanManage && !IsBusy && !HasPending;
    public bool CanMutate => CanEdit && Snapshot?.Status is { Supported: true, Installed: true, RecoveryRequired: false };
    public bool CanStop => CanEdit && Snapshot?.Status is { Supported: true, Installed: true };
    public bool CanRefresh => !_disposed && !_lifetime.IsCancellationRequested && CanRead && !IsBusy;
    public bool CanResume => !_disposed && !_lifetime.IsCancellationRequested && CanRead && !IsBusy && HasPending;
    partial void OnIsBusyChanged(bool value) => NotifyActions();
    partial void OnCanReadChanged(bool value) => NotifyActions();
    partial void OnCanManageChanged(bool value) => NotifyActions();
    partial void OnSnapshotChanged(SquidConfigurationSnapshot? value) => NotifyActions();
    private void NotifyActions()
    {
        foreach (var property in new[] { nameof(CanEdit), nameof(CanMutate), nameof(CanStop), nameof(CanRefresh), nameof(CanResume), nameof(HasPending) }) OnPropertyChanged(property);
        RefreshCommand.NotifyCanExecuteChanged(); LoadStoredCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); ValidateCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged(); EnableCommand.NotifyCanExecuteChanged(); DisableCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => CanRefresh ? WorkAsync(LoadAsync) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task LoadStoredAsync()
    {
        if (!CanEdit) return;
        if (HasUnsavedChanges && (ConfirmReloadAsync is null || !await ConfirmReloadAsync())) return;
        await WorkAsync(async () => { _initialized = false; await LoadAsync(); PreviewText = ""; AdoptExistingConfiguration = false; });
    }
    private async Task LoadAsync()
    {
        var snapshot = await client.ReadAsync(_lifetime.Token);
        Snapshot = snapshot;
        var status = snapshot.Status;
        StatusText = !status.Supported ? Text("unsupported") : !status.Installed ? Text("install_required")
            : status.RecoveryRequired ? Text("recovery_required")
            : string.Format(Text("status"), Text(status.Running ? "running" : "stopped"), Text(status.Enabled ? "enabled" : "disabled"),
                snapshot.State.DesiredRevision, snapshot.State.PublishedRevision?.ToString() ?? "—",
                Text(snapshot.MatchesPublishedConfiguration ? "matching" : "unconfirmed"));
        if (!_initialized)
        {
            var configuration = snapshot.State.Configuration ?? new SquidConfiguration();
            BindAddress = configuration.BindAddress; Port = configuration.Port.ToString(); AllowRemoteClients = configuration.AllowRemoteClients;
            AllowedNetworks = string.Join('\n', configuration.AllowedNetworks); AllowedDomains = string.Join('\n', configuration.AllowedDomains);
            DeniedDomains = string.Join('\n', configuration.DeniedDomains); UpstreamHost = configuration.Upstream?.Host ?? "";
            UpstreamPort = (configuration.Upstream?.Port ?? 7890).ToString(); _initialized = true;
        }
    }
    public SquidConfiguration BuildConfiguration()
    {
        if (!int.TryParse(Port, out var port) || (!string.IsNullOrWhiteSpace(UpstreamHost) && !int.TryParse(UpstreamPort, out _)))
            throw new InvalidOperationException(Text("invalid_port"));
        var configuration = new SquidConfiguration
        {
            BindAddress = BindAddress.Trim(), Port = port, AllowRemoteClients = AllowRemoteClients,
            AllowedNetworks = Lines(AllowedNetworks), AllowedDomains = Lines(AllowedDomains), DeniedDomains = Lines(DeniedDomains),
            Upstream = string.IsNullOrWhiteSpace(UpstreamHost) ? null : new(UpstreamHost.Trim(), int.Parse(UpstreamPort)),
        };
        var preview = SquidConfigurationRenderer.Preview(configuration);
        if (!preview.Valid) throw new InvalidOperationException(string.Join('\n', preview.Errors));
        return configuration;
    }
    private static string[] Lines(string value) => value.Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task PreviewAsync() => !CanEdit ? Task.CompletedTask : WorkAsync(async () =>
    {
        var preview = await client.PreviewAsync(BuildConfiguration(), _lifetime.Token);
        PreviewText = preview.Configuration ?? string.Join('\n', preview.Errors);
        ResultText = Text(preview.Valid ? "preview_ready" : "invalid");
    });
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task ValidateAsync() => !CanStop ? Task.CompletedTask : WorkAsync(async () =>
    {
        var result = await client.ValidateAsync(BuildConfiguration(), _lifetime.Token);
        ResultText = Text(result.Valid ? "validation_ok" : "invalid") + "\n" + result.ProblemCode + "\n" + result.Diagnostics;
    });
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private Task ApplyAsync() => !CanMutate ? Task.CompletedTask : WorkAsync(async () =>
    {
        var configuration = BuildConfiguration();
        PreviewText = SquidConfigurationRenderer.Preview(configuration).Configuration!;
        if (ConfirmApplyAsync is null || !await ConfirmApplyAsync()) return;
        Begin(GatewayProxyRoutes.Apply, new ApplySquidConfigurationRequest(configuration, AdoptExistingConfiguration));
        await SubmitAndWatchAsync();
    });
    [RelayCommand(CanExecute = nameof(CanMutate))] private Task StartAsync() => LifecycleAsync(SquidServiceAction.Start);
    [RelayCommand(CanExecute = nameof(CanStop))] private Task StopAsync() => LifecycleAsync(SquidServiceAction.Stop);
    [RelayCommand(CanExecute = nameof(CanMutate))] private Task RestartAsync() => LifecycleAsync(SquidServiceAction.Restart);
    [RelayCommand(CanExecute = nameof(CanMutate))] private Task ReloadAsync() => LifecycleAsync(SquidServiceAction.Reload);
    [RelayCommand(CanExecute = nameof(CanMutate))] private Task EnableAsync() => LifecycleAsync(SquidServiceAction.Enable);
    [RelayCommand(CanExecute = nameof(CanStop))] private Task DisableAsync() => LifecycleAsync(SquidServiceAction.Disable);
    private Task LifecycleAsync(SquidServiceAction action) => !(action is SquidServiceAction.Stop or SquidServiceAction.Disable ? CanStop : CanMutate) ? Task.CompletedTask : WorkAsync(async () =>
    {
        Begin(GatewayProxyRoutes.Lifecycle, new SquidLifecycleRequest(action));
        await SubmitAndWatchAsync();
    });
    private void Begin(string route, object body)
    {
        _pendingRoute = route; _pendingBody = body; _pendingKey = Guid.NewGuid().ToString("N"); NotifyActions();
    }
    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => CanResume ? WorkAsync(SubmitAndWatchAsync) : Task.CompletedTask;
    private async Task SubmitAndWatchAsync()
    {
        if (_operation is null)
        {
            try
            {
                var accepted = await client.SubmitAsync(_pendingRoute!, _pendingBody!, _pendingKey!, _lifetime.Token);
                _operation = accepted.OperationId;
            }
            catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                _pendingKey = null; _pendingBody = null; _pendingRoute = null; NotifyActions(); throw;
            }
        }
        for (var attempt = 0; attempt < 90; attempt++)
        {
            var operation = await client.OperationAsync(_operation.Value, _lifetime.Token);
            if (operation.OperationId != _operation || !operation.Kind.StartsWith("squid.", StringComparison.Ordinal)) throw new InvalidDataException(Text("invalid"));
            ResultText = $"{Text("operation")}: {operation.OperationId}\n{Text("operation_" + operation.State.ToString().ToLowerInvariant())}\n{operation.ProblemCode}";
            if (operation.State is not (ProxyOperationState.Queued or ProxyOperationState.Running))
            {
                _operation = null; _pendingKey = null; _pendingBody = null; _pendingRoute = null; NotifyActions();
                AdoptExistingConfiguration = false;
                await LoadAsync(); return;
            }
            await Task.Delay(1000, _lifetime.Token);
        }
        ResultText += "\n" + Text("pending");
    }
    private async Task WorkAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ResultText = exception.Message + (HasPending ? "\n" + Text("pending") + "\n" + (_operation?.ToString() ?? _pendingKey) : "");
            if (!HasPending) { Snapshot = null; StatusText = Text("unavailable"); }
        }
        finally { IsBusy = false; }
    }
    private static string Text(string key) => LocalizedText.Get("squid." + key);
    public void InvalidateSession()
    {
        if (_disposed) return;
        _lifetime.Cancel(); _pendingBody = null; _pendingKey = null; _pendingRoute = null; _operation = null; CanRead = false; CanManage = false; Snapshot = null;
        BindAddress = ""; AllowedNetworks = ""; AllowedDomains = ""; DeniedDomains = ""; UpstreamHost = ""; PreviewText = "";
        ResultText = Text("unavailable"); StatusText = Text("unavailable");
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
