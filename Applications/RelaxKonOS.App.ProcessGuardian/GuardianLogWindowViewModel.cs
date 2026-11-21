using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.App.ProcessGuardian;

public sealed class GuardianLogWindowViewModel(
    IServerGuardian guardian, GuardianWorkloadDto workload, Func<string, string> localize) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _closed = new();
    private Task? _reading;
    private bool _disposed;
    private string _statusKey = "guardian.logs.connecting";
    private string? _error;

    public ObservableCollection<string> Lines { get; } = [];
    public string Title => string.Format(CultureInfo.CurrentCulture, localize("guardian.logs.title"), workload.Name);
    public string StatusText => _error is null ? localize(_statusKey)
        : string.Format(CultureInfo.CurrentCulture, localize(_statusKey), _error);

    public void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) =>
        OnPropertyChanged(string.Empty);

    public Task StartAsync() => _disposed ? Task.CompletedTask : _reading ??= ReadAsync(_closed.Token);

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in guardian.WatchLogsAsync(workload.Id, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) return;
                Lines.Clear();
                foreach (var log in snapshot)
                    Lines.Add($"{log.Timestamp.LocalDateTime:G} [{log.Stream}] {log.Message}");
                SetStatus("guardian.logs.live");
            }
            SetStatus("guardian.logs.disconnected");
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested) SetStatus("guardian.logs.disconnected");
        }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested) SetStatus("guardian.logs.failed", error.Message);
        }
    }

    private void SetStatus(string key, string? error = null)
    {
        _statusKey = key;
        _error = error;
        OnPropertyChanged(nameof(StatusText));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _closed.Cancel();
            if (_reading is not null) await _reading;
        }
        finally { _closed.Dispose(); }
    }
}
