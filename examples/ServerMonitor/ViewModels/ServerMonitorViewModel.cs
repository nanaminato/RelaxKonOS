using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Examples.ServerMonitor.Services;

namespace RelaxKonOS.Examples.ServerMonitor.ViewModels;

/// <summary>Sampling, formatting, and settings state for one Server Monitor window.</summary>
public sealed partial class ServerMonitorViewModel : ObservableObject
{
    private readonly IServerMonitor _monitor;
    private readonly MonitorSettingsStore _settingsStore;
    private MonitorSettings _settings;
    private CancellationTokenSource? _shutdown;
    private int _isRefreshing;

    public ServerMonitorViewModel(IServerMonitor monitor, MonitorSettings settings, MonitorSettingsStore settingsStore)
    {
        _monitor = monitor;
        _settings = settings.Normalize();
        _settingsStore = settingsStore;
        RefreshOptions = [
            new RefreshOption("每 1 秒（最快）", 1000), new RefreshOption("每 2 秒", 2000),
            new RefreshOption("每 5 秒", 5000), new RefreshOption("每 10 秒", 10000),
        ];
        HistoryOptions = [
            new HistoryOption("30 个样本", 30), new HistoryOption("60 个样本", 60),
            new HistoryOption("120 个样本", 120), new HistoryOption("240 个样本", 240),
        ];
        SelectedRefreshOption = RefreshOptions.First(option => option.Milliseconds == _settings.RefreshIntervalMilliseconds);
        SelectedHistoryOption = HistoryOptions.First(option => option.Samples == _settings.HistoryLength);
    }

    public ObservableCollection<double> CpuHistory { get; } = [];
    public ObservableCollection<double> MemoryHistory { get; } = [];
    public ObservableCollection<double> NetworkHistory { get; } = [];
    public IReadOnlyList<RefreshOption> RefreshOptions { get; }
    public IReadOnlyList<HistoryOption> HistoryOptions { get; }
    public ObservableCollection<MetricRow> CpuCores { get; } = [];
    public ObservableCollection<MetricRow> Disks { get; } = [];
    public ObservableCollection<MetricRow> Networks { get; } = [];
    public ObservableCollection<MetricRow> Gpus { get; } = [];

    [ObservableProperty] private string _statusText = "正在连接 RelaxKonOS 服务器性能指标…";
    [ObservableProperty] private string _lastUpdatedText = "等待首个采样结果";
    [ObservableProperty] private string _cpuPercentText = "—";
    [ObservableProperty] private string _memoryPercentText = "—";
    [ObservableProperty] private string _networkRateText = "—";
    [ObservableProperty] private string _uptimeText = "运行时间：—";
    [ObservableProperty] private string _gpuHint = "未检测到 GPU 数据。";
    [ObservableProperty] private RefreshOption? _selectedRefreshOption;
    [ObservableProperty] private HistoryOption? _selectedHistoryOption;

    public async Task StartAsync(CancellationToken closed)
    {
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(closed);
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await RefreshCoreAsync(_shutdown.Token);
                await Task.Delay(_settings.RefreshIntervalMilliseconds, _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _shutdown.Dispose();
            _shutdown = null;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(_shutdown?.Token ?? CancellationToken.None);

    [RelayCommand]
    private void ClearHistory()
    {
        CpuHistory.Clear();
        MemoryHistory.Clear();
        NetworkHistory.Clear();
    }

    partial void OnSelectedRefreshOptionChanged(RefreshOption? value)
    {
        if (value is null) return;
        SaveSettings(_settings with { RefreshIntervalMilliseconds = value.Milliseconds });
    }

    partial void OnSelectedHistoryOptionChanged(HistoryOption? value)
    {
        if (value is null) return;
        SaveSettings(_settings with { HistoryLength = value.Samples });
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0)
            return;
        try
        {
            var result = await _monitor.GetSnapshotAsync(cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyResult(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = "无法连接服务器性能指标服务。" );
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    private void ApplyResult(ServerMetricsResult result)
    {
        if (result.Status == AppCapabilityResult.PermissionDenied)
        {
            StatusText = "没有权限读取性能指标。请在 设置 → 应用程序 → Server Monitor 中授权。";
            return;
        }
        if (result.Status != AppCapabilityResult.Succeeded || result.Snapshot is null)
        {
            StatusText = "服务器性能指标暂时不可用。";
            return;
        }

        var snapshot = result.Snapshot;
        var send = snapshot.Networks.Sum(network => network.SendRateBytesPerSecond);
        var receive = snapshot.Networks.Sum(network => network.ReceiveRateBytesPerSecond);
        var totalNetwork = send + receive;
        AppendHistory(CpuHistory, snapshot.CpuPercent);
        AppendHistory(MemoryHistory, snapshot.MemoryPercent);
        AppendHistory(NetworkHistory, totalNetwork);

        CpuPercentText = $"{snapshot.CpuPercent:0.0}%";
        MemoryPercentText = $"{snapshot.MemoryPercent:0.0}%";
        NetworkRateText = FormatRate(totalNetwork);
        UptimeText = $"{snapshot.CpuCoreCount} 核 · 已运行 {FormatUptime(snapshot.UptimeSeconds)}";
        Replace(CpuCores, snapshot.CpuPerCorePercent.Select((value, index) => new MetricRow($"CPU {index}", $"{value:0.0}%")));
        Replace(Disks, snapshot.Disks.Select(disk => new MetricRow(disk.Name,
            $"{disk.Percent:0}% 已用 · {FormatBytes(disk.UsedBytes)} / {FormatBytes(disk.TotalBytes)}")));
        Replace(Networks, snapshot.Networks.Select(network => new MetricRow(network.Name,
            $"↓ {FormatRate(network.ReceiveRateBytesPerSecond)}    ↑ {FormatRate(network.SendRateBytesPerSecond)}")));
        Replace(Gpus, snapshot.Gpus.Select(gpu => new MetricRow(gpu.Name,
            $"{(gpu.UsagePercent is { } usage ? $"{usage:0}%" : "利用率不可用")}" +
            (gpu.TemperatureCelsius is { } temperature ? $" · {temperature:0}°C" : string.Empty))));
        GpuHint = Gpus.Count == 0 ? "未检测到 GPU 数据。" : string.Empty;
        StatusText = "正在显示实时服务器性能指标";
        LastUpdatedText = $"上次更新：{snapshot.Timestamp.LocalDateTime:HH:mm:ss} · 当前刷新间隔 {_settings.RefreshIntervalMilliseconds / 1000.0:0.#} 秒";
    }

    private void SaveSettings(MonitorSettings settings)
    {
        _settings = settings.Normalize();
        _settingsStore.Save(_settings);
    }

    private void AppendHistory(ObservableCollection<double> history, double value)
    {
        history.Add(double.IsFinite(value) ? Math.Max(value, 0) : 0);
        while (history.Count > _settings.HistoryLength)
            history.RemoveAt(0);
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source) destination.Add(item);
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(value, 0);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }

    private static string FormatRate(long value) => $"{FormatBytes(value)}/s";
    private static string FormatUptime(long seconds) => TimeSpan.FromSeconds(Math.Max(seconds, 0)).ToString(@"d\.hh\:mm\:ss");
}

public sealed record RefreshOption(string Label, int Milliseconds)
{
    public override string ToString() => Label;
}

public sealed record HistoryOption(string Label, int Samples)
{
    public override string ToString() => Label;
}

public sealed record MetricRow(string Name, string Value);
