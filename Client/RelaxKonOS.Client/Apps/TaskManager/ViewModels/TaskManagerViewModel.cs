using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Apps.TaskManager.ViewModels;

/// <summary>性能数据来自服务端统一采样器；进程列表独立查询。</summary>
public sealed partial class TaskManagerViewModel : LocalizedObservableObject, IAsyncDisposable
{
    private readonly ITaskManagerClient _client;
    private readonly PerformanceStream _stream;
    private readonly DispatcherTimer _processTimer;
    private List<ProcessInfoDto> _allProcesses = [];
    private readonly Dictionary<(int Id, DateTimeOffset? StartTime), ProcessTreeItemViewModel> _processRows = [];
    private int _refreshingProcesses;
    private bool _rebuildingProcessRows;
    private long _lastSequence;
    private int _disposed;
    private readonly IHostElevationBroker _elevation;
    private readonly IAuthSession _session;
    private int _processModeGeneration;
    private Guid? _processSessionId;
    private string? _processServiceId;
    private bool _switchingProcessMode;

    public TaskManagerViewModel(ITaskManagerClient client, PerformanceStream stream, IHostElevationBroker elevation, IAuthSession session)
    {
        _client = client;
        _stream = stream;
        _elevation = elevation;
        _session = session;
        _processSessionId = session.CurrentSession?.Id;
        _processServiceId = session.ServiceId;
        _session.StateChanged += OnAuthSessionChanged;
        _stream.SnapshotReceived += OnSnapshotReceived;
        _stream.Reconnected += OnStreamReconnected;
        _stream.Disconnected += OnStreamDisconnected;
        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _processTimer.Tick += async (_, _) => await RefreshProcessesAsync();
        FilteredProcesses = [];
        PerformanceItems = [];
    }

    public ObservableCollection<ProcessTreeItemViewModel> FilteredProcesses { get; }
    public ObservableCollection<PerformanceResourceItem> PerformanceItems { get; }

    [ObservableProperty] private PerformanceInfoDto? _info;
    [ObservableProperty] private PerformanceRealtimeSnapshotDto? _snapshot;
    [ObservableProperty] private ProcessTreeItemViewModel? _selectedProcess;
    [ObservableProperty] private PerformanceResourceItem? _selectedPerformanceItem;
    [ObservableProperty] private bool _isAutoRefresh = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.Ref("task_manager.status.collecting");
    [ObservableProperty] private LocalizedStatus _connectionStatus = LocalizedText.Ref("task_manager.connection.initializing");
    [ObservableProperty] private string _killFeedback = string.Empty;
    [ObservableProperty] private TaskManagerTab _activeTab = TaskManagerTab.Performance;
    [ObservableProperty] private string _processFilter = string.Empty;
    [ObservableProperty] private int _processTotalCount;
    [ObservableProperty] private bool _isAdministratorMode;

    public string ProcessModeLabel => LocalizedText.Get(IsAdministratorMode
        ? "task_manager.process.normal_mode" : "task_manager.process.administrator_mode");
    partial void OnIsAdministratorModeChanged(bool value) => OnPropertyChanged(nameof(ProcessModeLabel));

    [RelayCommand]
    private async Task ToggleProcessModeAsync()
    {
        if (IsLoading || KillProcessCommand.IsRunning) return;
        var owner = _session.CurrentSession?.Id;
        var service = _session.ServiceId;
        var nextMode = !IsAdministratorMode;
        _switchingProcessMode = true;
        IsLoading = true;
        try
        {
            // Probe a read, never a termination. The broker first asks for passwordless
            // authorization; only the server's password-required response opens a prompt.
            if (nextMode)
                await _elevation.ExecuteAsync(HostElevationCapability.ProcessManage, "host/processes",
                    () => _client.QueryProcessesAsync(pageSize: 1, elevated: true));
            if (_disposed != 0 || owner != _session.CurrentSession?.Id || service != _session.ServiceId) return;
            IsAdministratorMode = nextMode;
            _processModeGeneration++;
            foreach (var row in _processRows.Values) { row.ExpandedStateChanged -= OnProcessExpandedStateChanged; row.Dispose(); }
            _processRows.Clear(); _allProcesses.Clear(); FilteredProcesses.Clear(); SelectedProcess = null;
            ProcessTotalCount = 0; KillFeedback = string.Empty;
        }
        catch (Exception ex)
        {
            if (_disposed == 0 && owner == _session.CurrentSession?.Id && service == _session.ServiceId)
                StatusText = LocalizedText.Ref("task_manager.status.collect_failed", ex.Message);
            return;
        }
        finally { _switchingProcessMode = false; IsLoading = false; }
        await RefreshProcessesAsync();
    }

    public async Task StartAsync()
    {
        await RefreshPerformanceAsync();
        try { await _stream.StartAsync(); ConnectionStatus = LocalizedText.Ref("task_manager.connection.live"); }
        catch (Exception ex)
        {
            ConnectionStatus = LocalizedText.Ref("task_manager.connection.snapshot");
            StatusText = LocalizedText.Ref("task_manager.status.collect_failed", ex.Message);
        }
    }

    public void Stop() => _ = DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _processTimer.Stop();
        _session.StateChanged -= OnAuthSessionChanged;
        _stream.SnapshotReceived -= OnSnapshotReceived;
        _stream.Reconnected -= OnStreamReconnected;
        _stream.Disconnected -= OnStreamDisconnected;
        foreach (var row in _processRows.Values) row.Dispose();
        _processRows.Clear();
        await _stream.DisposeAsync();
    }

    [RelayCommand] private async Task RefreshAsync()
    {
        if (ActiveTab == TaskManagerTab.Processes) await RefreshProcessesAsync();
        else await RefreshPerformanceAsync();
    }

    [RelayCommand(CanExecute = nameof(CanKill))]
    private async Task KillProcessAsync()
    {
        var process = SelectedProcess?.Process;
        if (process?.StartTime is null) return;
        var owner = _session.CurrentSession?.Id;
        var service = _session.ServiceId;
        KillFeedback = LocalizedText.Format("task_manager.process.terminating", process.Name, process.Id);
        try
        {
            var result = await ProcessTerminationFlow.ExecuteAsync(IsAdministratorMode,
                elevated => _client.KillProcessAsync(process.Id, process.StartTime.Value, elevated),
                operation => _elevation.ExecuteAsync(HostElevationCapability.ProcessManage, "host/processes", operation),
                () => _disposed == 0 && owner == _session.CurrentSession?.Id && service == _session.ServiceId);
            if (_disposed != 0 || owner != _session.CurrentSession?.Id || service != _session.ServiceId) return;
            if (result.Success) { KillFeedback = LocalizedText.Format("task_manager.process.terminated", process.Name, process.Id); SelectedProcess = null; }
            else if (result.RequiresElevation) KillFeedback = LocalizedText.Format("task_manager.process.elevation_required", process.Name, process.Id, TerminationProblem(result));
            else KillFeedback = LocalizedText.Format("task_manager.process.termination_failed", TerminationProblem(result));
        }
        catch (Exception ex)
        {
            if (_disposed == 0 && owner == _session.CurrentSession?.Id && service == _session.ServiceId)
                KillFeedback = LocalizedText.Format("task_manager.process.termination_failed", ex.Message);
        }
        finally
        {
            // A transport failure leaves the outcome unknown. Reconcile without replaying the mutation.
            if (_disposed == 0 && owner == _session.CurrentSession?.Id && service == _session.ServiceId)
                await RefreshProcessesAsync();
        }
    }

    private static string TerminationProblem(KillProcessResultDto result) => LocalizedText.Get(result.ProblemCode switch
    {
        "process.instance_changed" or "process.not_found" => "task_manager.termination.instance_changed",
        "process.permission_denied" => "task_manager.termination.permission_denied",
        "process.termination_unverified" => "task_manager.termination.unverified",
        _ => "task_manager.termination.failed",
    });
    private bool CanKill => !IsLoading && SelectedProcess?.Process.StartTime is not null;
    partial void OnIsLoadingChanged(bool value) => KillProcessCommand.NotifyCanExecuteChanged();
    partial void OnSelectedProcessChanged(ProcessTreeItemViewModel? value) => KillProcessCommand.NotifyCanExecuteChanged();
    [RelayCommand] private void SwitchToPerformance() => ActiveTab = TaskManagerTab.Performance;
    [RelayCommand] private void SwitchToProcesses() => ActiveTab = TaskManagerTab.Processes;
    [RelayCommand] private void ClearFilter() => ProcessFilter = string.Empty;

    partial void OnActiveTabChanged(TaskManagerTab value)
    {
        if (value == TaskManagerTab.Processes)
        {
            _ = RefreshProcessesAsync();
            if (IsAutoRefresh) _processTimer.Start();
        }
        else _processTimer.Stop();
    }

    partial void OnIsAutoRefreshChanged(bool value)
    {
        if (value && ActiveTab == TaskManagerTab.Processes && !_processTimer.IsEnabled) _processTimer.Start();
        else if (!value && _processTimer.IsEnabled) _processTimer.Stop();
    }

    partial void OnProcessFilterChanged(string value) => RebuildFilteredProcesses(SelectedProcess);

    private async Task RefreshPerformanceAsync()
    {
        try
        {
            var infoTask = _client.GetPerformanceInfoAsync();
            var historyTask = _client.GetPerformanceHistoryAsync();
            await Task.WhenAll(infoTask, historyTask);
            Info = await infoTask;
            BuildPerformanceItems();
            var history = await historyTask;
            if (history.Count == 0)
            {
                try { ApplySnapshot(await _client.GetPerformanceSnapshotAsync(), true); }
                catch { ConnectionStatus = LocalizedText.Ref("task_manager.connection.awaiting_sample"); }
            }
            else ReplaceHistory(history);
            if (ConnectionStatus != LocalizedText.Ref("task_manager.connection.live")) ConnectionStatus = LocalizedText.Ref("task_manager.connection.snapshot_updated");
        }
        catch (Exception ex)
        {
            ConnectionStatus = LocalizedText.Ref("task_manager.connection.unavailable");
            StatusText = LocalizedText.Ref("task_manager.status.collect_failed", ex.Message);
        }
    }

    private async Task RefreshProcessesAsync()
    {
        if (_switchingProcessMode) return;
        if (Interlocked.CompareExchange(ref _refreshingProcesses, 1, 0) != 0) return;
        IsLoading = true;
        var generation = _processModeGeneration;
        var owner = _session.CurrentSession?.Id;
        var service = _session.ServiceId;
        try
        {
            var page = await QueryAllProcessesAsync();
            if (_disposed != 0 || generation != _processModeGeneration || owner != _session.CurrentSession?.Id || service != _session.ServiceId) return;
            ProcessTotalCount = page.TotalCount;
            _allProcesses = page.Items.ToList();
            RebuildFilteredProcesses(SelectedProcess);
            StatusText = LocalizedText.Ref("task_manager.status.updated", page.SampledAt.LocalDateTime, Snapshot?.Cpu.TotalPercent ?? 0, ProcessTotalCount);
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("task_manager.status.collect_failed", ex.Message); }
        finally { IsLoading = false; Interlocked.Exchange(ref _refreshingProcesses, 0); }
    }

    private async Task<ProcessPageDto> QueryAllProcessesAsync()
    {
        const int pageSize = 500;
        var elevated = IsAdministratorMode;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var first = await _client.QueryProcessesAsync(page: 1, pageSize: pageSize, sort: "memory", elevated: elevated);
            if (first.TotalCount <= first.Items.Count) return first;
            var pageCount = (first.TotalCount + pageSize - 1) / pageSize;
            var remaining = await Task.WhenAll(Enumerable.Range(2, pageCount - 1)
                .Select(page => _client.QueryProcessesAsync(page: page, pageSize: pageSize, sort: "memory", elevated: elevated)));
            var items = first.Items.Concat(remaining.SelectMany(page => page.Items)).ToArray();
            if (remaining.All(page => page.SampledAt == first.SampledAt && page.TotalCount == first.TotalCount)
                && items.Length == first.TotalCount && items.Select(item => item.Id).Distinct().Count() == items.Length)
                return first with { Items = items };
        }
        throw new InvalidOperationException("Process snapshot changed during pagination; refresh to retry.");
    }

    public async Task LoadProcessIconAsync(ProcessTreeItemViewModel row)
    {
        if (row.IsIconLoaded || row.IsDisposed) return;
        row.IsIconLoaded = true;
        if (row.Process.StartTime is not { } startTime) return;
        try
        {
            var icon = await _client.GetProcessIconAsync(row.Process.Id, startTime, IsAdministratorMode);
            if (row.IsDisposed || icon is null || icon.Value.Data.Length == 0) return;
            using var stream = new MemoryStream(icon.Value.Data, writable: false);
            row.IconImage = icon.Value.ContentType == "image/svg+xml"
                ? new SvgImage { Source = SvgSource.Load(stream) }
                : new Bitmap(stream);
        }
        catch { /* The row keeps its category glyph when the host has no matching icon. */ }
    }

    private void OnSnapshotReceived(PerformanceRealtimeSnapshotDto snapshot) => Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot, false));
    private void OnStreamReconnected() => Dispatcher.UIThread.Post(async () => { ConnectionStatus = LocalizedText.Ref("task_manager.connection.backfilling"); await RefreshPerformanceAsync(); ConnectionStatus = LocalizedText.Ref("task_manager.connection.live"); });
    private void OnStreamDisconnected() => Dispatcher.UIThread.Post(() => ConnectionStatus = LocalizedText.Ref("task_manager.connection.disconnected"));

    private void BuildPerformanceItems()
    {
        (PerformanceResourceKind Kind, string Id)? selected = SelectedPerformanceItem is null
            ? null
            : (SelectedPerformanceItem.Kind, SelectedPerformanceItem.Id);
        PerformanceItems.Clear();
        if (Info is null) return;
        PerformanceItems.Add(new(PerformanceResourceKind.Cpu, "cpu", "CPU", Info.Cpu.Model ?? LocalizedText.Get("task_manager.processor"), Color.Parse("#0078D4")));
        PerformanceItems.Add(new(PerformanceResourceKind.Memory, "memory", LocalizedText.Get("task_manager.memory"), FormatBytes(Info.Memory.TotalBytes), Color.Parse("#8A2BE2")));
        foreach (var disk in Info.Disks.OrderBy(DiskSortKey, StringComparer.OrdinalIgnoreCase)) PerformanceItems.Add(new(PerformanceResourceKind.Disk, disk.Id, DiskTitle(disk), disk.Model ?? LocalizedText.Get("task_manager.disk_io"), Color.Parse("#A65E00")));
        foreach (var network in Info.Networks) PerformanceItems.Add(new(PerformanceResourceKind.Network, network.Id, network.Name, LocalizedText.Get("task_manager.network_adapter"), Color.Parse("#C45A00")));
        SelectedPerformanceItem = selected is null ? PerformanceItems.FirstOrDefault() : PerformanceItems.FirstOrDefault(x => x.Kind == selected.Value.Kind && x.Id == selected.Value.Id) ?? PerformanceItems.FirstOrDefault();
    }

    private void ReplaceHistory(IReadOnlyList<PerformanceRealtimeSnapshotDto> history)
    {
        foreach (var item in PerformanceItems) item.ClearHistory();
        _lastSequence = 0;
        foreach (var snapshot in history.OrderBy(x => x.Sequence)) ApplySnapshot(snapshot, false);
    }

    private void ApplySnapshot(PerformanceRealtimeSnapshotDto snapshot, bool resetHistory)
    {
        if (!resetHistory && snapshot.Sequence <= _lastSequence) return;
        if (resetHistory) { foreach (var item in PerformanceItems) item.ClearHistory(); _lastSequence = 0; }
        _lastSequence = Math.Max(_lastSequence, snapshot.Sequence);
        Snapshot = snapshot;
        foreach (var item in PerformanceItems) UpdateItem(item, snapshot);
        StatusText = LocalizedText.Ref("task_manager.status.updated", snapshot.Timestamp.LocalDateTime, snapshot.Cpu.TotalPercent, ProcessTotalCount);
    }

    private void UpdateItem(PerformanceResourceItem item, PerformanceRealtimeSnapshotDto snapshot)
    {
        switch (item.Kind)
        {
            case PerformanceResourceKind.Cpu:
                var cpu = snapshot.Cpu;
                item.Update(cpu.TotalPercent, 100, $"{cpu.TotalPercent:0}%", $"{cpu.TotalPercent:0}%  {FormatFrequency(cpu.CurrentFrequencyMHz)}",
                    LocalizedText.Get("task_manager.metric.utilization"), $"{cpu.TotalPercent:0}%", LocalizedText.Get("task_manager.metric.speed"), FormatFrequency(cpu.CurrentFrequencyMHz),
                    LocalizedText.Get("task_manager.metric.processes"), FormatCount(cpu.ProcessCount), LocalizedText.Get("task_manager.threads"), FormatCount(cpu.ThreadCount));
                item.SetAdditionalDetails(
                    (LocalizedText.Get("task_manager.metric.handles"), FormatCount(cpu.HandleCount)),
                    (LocalizedText.Get("task_manager.metric.uptime"), FormatUptime(snapshot.UptimeSeconds)),
                    (LocalizedText.Get("task_manager.metric.base_speed"), FormatFrequency(Info?.Cpu.BaseFrequencyMHz)),
                    (LocalizedText.Get("task_manager.metric.sockets"), FormatCount(Info?.Cpu.SocketCount)),
                    (LocalizedText.Get("task_manager.metric.cores"), FormatCount(Info?.Cpu.PhysicalCoreCount)),
                    (LocalizedText.Get("task_manager.metric.logical_processors"), FormatCount(Info?.Cpu.LogicalProcessorCount)),
                    (LocalizedText.Get("task_manager.metric.virtualization"), FormatBoolean(Info?.Cpu.VirtualizationEnabled)),
                    (LocalizedText.Get("task_manager.metric.l1_cache"), FormatBytes(Info?.Cpu.L1CacheBytes)),
                    (LocalizedText.Get("task_manager.metric.l2_cache"), FormatBytes(Info?.Cpu.L2CacheBytes)),
                    (LocalizedText.Get("task_manager.metric.l3_cache"), FormatBytes(Info?.Cpu.L3CacheBytes)));
                break;
            case PerformanceResourceKind.Memory:
                var memory = snapshot.Memory;
                var percent = memory.TotalBytes <= 0 ? 0 : memory.UsedBytes * 100d / memory.TotalBytes;
                item.Update(percent, 100, $"{FormatBytes(memory.UsedBytes)} / {FormatBytes(memory.TotalBytes)}", $"{percent:0}%",
                    LocalizedText.Get("task_manager.metric.used"), FormatBytes(memory.UsedBytes), LocalizedText.Get("task_manager.metric.available"), FormatBytes(memory.AvailableBytes),
                    LocalizedText.Get("task_manager.metric.cached"), FormatBytes(memory.CachedBytes), LocalizedText.Get("task_manager.metric.swap_used"), FormatBytes(memory.SwapUsedBytes));
                item.SetAdditionalDetails(
                    (LocalizedText.Get("task_manager.metric.total_memory"), FormatBytes(memory.TotalBytes)),
                    (LocalizedText.Get("task_manager.metric.buffers"), FormatBytes(memory.BufferedBytes)),
                    (LocalizedText.Get("task_manager.metric.swap_total"), FormatBytes(memory.SwapTotalBytes)));
                break;
            case PerformanceResourceKind.Disk:
                var disk = snapshot.Disks.FirstOrDefault(x => x.Id == item.Id);
                if (disk is not null)
                {
                    var rate = disk.ReadBytesPerSecond + disk.WriteBytesPerSecond;
                    item.Update(rate, Math.Max(1_048_576, rate * 1.25), LocalizedText.Format("task_manager.metric.read", FormatRate(disk.ReadBytesPerSecond)), LocalizedText.Format("task_manager.metric.write", FormatRate(disk.WriteBytesPerSecond)), LocalizedText.Get("task_manager.metric.read_speed"), FormatRate(disk.ReadBytesPerSecond), LocalizedText.Get("task_manager.metric.write_speed"), FormatRate(disk.WriteBytesPerSecond), LocalizedText.Get("task_manager.metric.active_time"), FormatPercent(disk.ActivityPercent), LocalizedText.Get("task_manager.metric.average_response_time"), FormatLatency(disk.LatencyMs));
                }
                break;
            case PerformanceResourceKind.Network:
                var network = snapshot.Networks.FirstOrDefault(x => x.Id == item.Id);
                if (network is not null)
                {
                    var rate = network.ReceiveBytesPerSecond + network.SendBytesPerSecond;
                    item.Update(rate, Math.Max(1_048_576, rate * 1.25), LocalizedText.Format("task_manager.metric.send", FormatRate(network.SendBytesPerSecond)), LocalizedText.Format("task_manager.metric.receive", FormatRate(network.ReceiveBytesPerSecond)), LocalizedText.Get("task_manager.metric.sent"), FormatRate(network.SendBytesPerSecond), LocalizedText.Get("task_manager.metric.received"), FormatRate(network.ReceiveBytesPerSecond), LocalizedText.Get("task_manager.metric.bytes_sent"), FormatBytes(network.BytesSent), LocalizedText.Get("task_manager.metric.bytes_received"), FormatBytes(network.BytesReceived));
                }
                break;
        }
    }

    public string ProcessSortColumn { get; private set; } = "MemoryBytes";
    public bool ProcessSortDescending { get; private set; } = true;

    public void SortProcesses(string column)
    {
        ProcessSortDescending = column == ProcessSortColumn ? !ProcessSortDescending
            : column is "CpuPercent" or "MemoryBytes" or "ThreadCount";
        ProcessSortColumn = column;
        RebuildFilteredProcesses(SelectedProcess);
    }

    private IEnumerable<ProcessTreeItemViewModel> SortProcessRows(IEnumerable<ProcessTreeItemViewModel> rows)
    {
        var comparer = Comparer<ProcessTreeItemViewModel>.Create((left, right) =>
        {
            var a = left.Process;
            var b = right.Process;
            var result = ProcessSortColumn switch
            {
                "Name" => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
                "Id" => a.Id.CompareTo(b.Id),
                "CpuPercent" => a.CpuPercent.CompareTo(b.CpuPercent),
                "MemoryBytes" => a.MemoryBytes.CompareTo(b.MemoryBytes),
                "UserName" => StringComparer.CurrentCultureIgnoreCase.Compare(a.UserName, b.UserName),
                "ThreadCount" => a.ThreadCount.CompareTo(b.ThreadCount),
                _ => 0,
            };
            return ProcessSortDescending ? -result : result;
        });
        return rows.OrderBy(row => row, comparer).ThenBy(row => row.Process.Id);
    }

    private void RebuildFilteredProcesses(ProcessTreeItemViewModel? selected)
    {
        if (_rebuildingProcessRows) return;
        _rebuildingProcessRows = true;
        try
        {
            (int Id, DateTimeOffset? StartTime)? selectedKey = selected is null
                ? null : (selected.Process.Id, selected.Process.StartTime);
            var nextKeys = _allProcesses.Select(process => (process.Id, process.StartTime)).ToHashSet();
            foreach (var stale in _processRows.Keys.Where(key => !nextKeys.Contains(key)).ToArray())
            {
                var old = _processRows[stale];
                old.ExpandedStateChanged -= OnProcessExpandedStateChanged;
                old.Dispose();
                _processRows.Remove(stale);
            }

            var rows = new List<ProcessTreeItemViewModel>(_allProcesses.Count);
            var byId = new Dictionary<int, ProcessTreeItemViewModel>();
            foreach (var process in _allProcesses)
            {
                var key = (process.Id, process.StartTime);
                if (!_processRows.TryGetValue(key, out var row))
                {
                    row = new ProcessTreeItemViewModel(process);
                    row.ExpandedStateChanged += OnProcessExpandedStateChanged;
                    _processRows.Add(key, row);
                }
                else row.Update(process, row.Depth, row.HasChildren);
                rows.Add(row);
                byId[process.Id] = row;
            }

            var parentIds = new Dictionary<int, int>();
            var children = rows.ToDictionary(row => row.Process.Id, _ => new List<ProcessTreeItemViewModel>());
            var roots = new List<ProcessTreeItemViewModel>();
            foreach (var row in rows)
            {
                var process = row.Process;
                if (process.ParentProcessId is { } parentId && parentId != process.Id && byId.TryGetValue(parentId, out var parent)
                    && string.Equals(parent.Process.Name, process.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(parent.Process.UserName, process.UserName, StringComparison.OrdinalIgnoreCase)
                    && (parent.Process.StartTime is null || process.StartTime is null || parent.Process.StartTime <= process.StartTime))
                {
                    parentIds[process.Id] = parentId;
                    children[parentId].Add(row);
                }
                else roots.Add(row);
            }
            // Present related instances as a shallow application group, rather than an OS ancestry tree.
            foreach (var row in rows)
            {
                var id = row.Process.Id;
                var ancestry = new HashSet<int> { id };
                while (parentIds.TryGetValue(id, out var parentId) && ancestry.Add(parentId)) id = parentId;
                if (id == row.Process.Id || parentIds.ContainsKey(id)) continue;
                var previousParent = parentIds[row.Process.Id];
                if (previousParent == id) continue;
                children[previousParent].Remove(row);
                children[id].Add(row);
                parentIds[row.Process.Id] = id;
            }
            foreach (var row in rows)
            {
                row.Update(row.Process, row.Depth, children[row.Process.Id].Count > 0);
                row.GroupProcessCount = 1 + children[row.Process.Id].Count;
            }

            var filter = ProcessFilter.Trim();
            bool Matches(ProcessInfoDto process) => string.IsNullOrWhiteSpace(filter)
                || process.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || process.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (process.UserName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
            ProcessTotalCount = rows.Count(row => Matches(row.Process));

            var included = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(filter)) included.UnionWith(byId.Keys);
            else
            {
                foreach (var row in rows.Where(row => Matches(row.Process)))
                {
                    var id = row.Process.Id;
                    while (included.Add(id) && parentIds.TryGetValue(id, out var parentId)) id = parentId;
                }
            }

            FilteredProcesses.Clear();
            var visited = new HashSet<int>();
            void AddVisible(ProcessTreeItemViewModel row, int depth)
            {
                var id = row.Process.Id;
                if (!included.Contains(id) || !visited.Add(id)) return;
                row.Update(row.Process, depth, children[id].Count > 0);
                var hasIncludedChild = children[id].Any(child => included.Contains(child.Process.Id));
                var showChildren = row.IsExpanded || (!string.IsNullOrWhiteSpace(filter) && hasIncludedChild);
                FilteredProcesses.Add(row);
                if (showChildren)
                    foreach (var child in SortProcessRows(children[id])) AddVisible(child, depth + 1);
            }

            var reachable = new HashSet<int>();
            void MarkSubtree(ProcessTreeItemViewModel root)
            {
                var pending = new Stack<ProcessTreeItemViewModel>();
                pending.Push(root);
                while (pending.TryPop(out var row))
                    if (reachable.Add(row.Process.Id))
                        foreach (var child in children[row.Process.Id]) pending.Push(child);
            }
            foreach (var root in SortProcessRows(roots)) { MarkSubtree(root); AddVisible(root, 0); }
            // Only disconnected cycles need a synthetic root. Collapsed descendants already
            // belong to a root and must remain hidden.
            foreach (var row in SortProcessRows(rows))
                if (!reachable.Contains(row.Process.Id)) { MarkSubtree(row); AddVisible(row, 0); }
            SelectedProcess = selectedKey is { } identity
                ? FilteredProcesses.FirstOrDefault(row => row.Process.Id == identity.Id && row.Process.StartTime == identity.StartTime)
                : null;
        }
        finally { _rebuildingProcessRows = false; }
    }

    private void OnProcessExpandedStateChanged(object? sender, EventArgs e) => RebuildFilteredProcesses(SelectedProcess);

    private void OnAuthSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed != 0) return;
        if (_session.State == AuthSessionState.Authenticated && _processSessionId == _session.CurrentSession?.Id
            && _processServiceId == _session.ServiceId) return;
        _processSessionId = _session.CurrentSession?.Id; _processServiceId = _session.ServiceId;
        IsAdministratorMode = false; _processModeGeneration++;
        foreach (var row in _processRows.Values) { row.ExpandedStateChanged -= OnProcessExpandedStateChanged; row.Dispose(); }
        _processRows.Clear(); _allProcesses.Clear(); FilteredProcesses.Clear(); SelectedProcess = null;
        ProcessTotalCount = 0; KillFeedback = string.Empty;
    });

    private static string FormatBytes(long? bytes) => bytes is null or < 0 ? "—" : Converters.BytesConverter.FormatBytes(bytes.Value);
    private static string FormatRate(long bytes) => Converters.BytesConverter.FormatBytes(bytes) + LocalizedText.Get("task_manager.metric.per_second");
    private static string FormatPercent(double? percent) => percent is null ? "—" : $"{percent.Value:0}%";
    private static string FormatLatency(double? milliseconds) => milliseconds is null ? "—" : $"{milliseconds.Value:0.0} ms";
    private string DiskSortKey(DiskInfoDto disk)
    {
        var firstWindowsVolume = disk.FilesystemIds
            .Select(id => Info?.Filesystems.FirstOrDefault(filesystem => filesystem.Id == id)?.MountPoint)
            .Where(mount => mount is { Length: >= 2 } && mount[1] == ':')
            .OrderBy(mount => mount, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return firstWindowsVolume is null ? $"~{disk.Id}" : $"{firstWindowsVolume}\0{disk.Id}";
    }
    private string DiskTitle(DiskInfoDto disk)
    {
        var title = disk.Id.StartsWith("windows-disk:", StringComparison.Ordinal)
            ? LocalizedText.Format("task_manager.disk_detail", disk.Id["windows-disk:".Length..])
            : disk.Name;
        var mounts = disk.FilesystemIds
            .Select(id => Info?.Filesystems.FirstOrDefault(filesystem => filesystem.Id == id))
            .Where(filesystem => filesystem is not null)
            .Select(filesystem => FormatMountName(filesystem!))
            .ToArray();
        return mounts.Length == 0 ? title : $"{title} ({string.Join(", ", mounts)})";
    }

    private static string FormatMountName(FilesystemInfoDto filesystem)
    {
        var mount = filesystem.MountPoint;
        return mount.Length >= 2 && mount[1] == ':' ? mount.TrimEnd('\\', '/') : mount;
    }
    private static string FormatFrequency(double? mhz) => mhz is null ? "—" : mhz >= 1000 ? $"{mhz / 1000:0.00} GHz" : $"{mhz:0} MHz";
    private static string FormatCount(long? value) => value is null or < 0 ? "—" : value.Value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    private static string FormatBoolean(bool? value) => value is null ? "—" : LocalizedText.Get(value.Value ? "task_manager.boolean.enabled" : "task_manager.boolean.disabled");
    private static string FormatUptime(long seconds) => Converters.UptimeConverter.Instance.Convert(seconds, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture)?.ToString() ?? "—";
}

public enum TaskManagerTab { Performance, Processes }
public enum PerformanceResourceKind { Cpu, Memory, Disk, Network }

public sealed partial class PerformanceResourceItem : LocalizedObservableObject
{
    public PerformanceResourceItem(PerformanceResourceKind kind, string id, string title, string subtitle, Color accentColor)
    { Kind = kind; Id = id; Title = title; Subtitle = subtitle; AccentColor = accentColor; History = []; }

    public PerformanceResourceKind Kind { get; }
    public bool IsCpu => Kind == PerformanceResourceKind.Cpu;
    public bool IsMemory => Kind == PerformanceResourceKind.Memory;
    public bool UsesStandardDetails => !IsCpu && !IsMemory;
    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public Color AccentColor { get; }
    public ObservableCollection<double> History { get; }
    [ObservableProperty] private LocalizedStatus _metric = LocalizedText.Ref("task_manager.metric.collecting");
    [ObservableProperty] private string _sideDetail = string.Empty;
    [ObservableProperty] private double _chartMaximum = 100;
    [ObservableProperty] private LocalizedStatus _detail1Label;
    [ObservableProperty] private string _detail1Value = "—";
    [ObservableProperty] private LocalizedStatus _detail2Label;
    [ObservableProperty] private string _detail2Value = "—";
    [ObservableProperty] private LocalizedStatus _detail3Label;
    [ObservableProperty] private string _detail3Value = "—";
    [ObservableProperty] private LocalizedStatus _detail4Label;
    [ObservableProperty] private string _detail4Value = "—";
    [ObservableProperty] private LocalizedStatus _detail5Label;
    [ObservableProperty] private string _detail5Value = "—";
    [ObservableProperty] private LocalizedStatus _detail6Label;
    [ObservableProperty] private string _detail6Value = "—";
    [ObservableProperty] private LocalizedStatus _detail7Label;
    [ObservableProperty] private string _detail7Value = "—";
    [ObservableProperty] private LocalizedStatus _detail8Label;
    [ObservableProperty] private string _detail8Value = "—";
    [ObservableProperty] private LocalizedStatus _detail9Label;
    [ObservableProperty] private string _detail9Value = "—";
    [ObservableProperty] private LocalizedStatus _detail10Label;
    [ObservableProperty] private string _detail10Value = "—";
    [ObservableProperty] private LocalizedStatus _detail11Label;
    [ObservableProperty] private string _detail11Value = "—";
    [ObservableProperty] private LocalizedStatus _detail12Label;
    [ObservableProperty] private string _detail12Value = "—";
    [ObservableProperty] private LocalizedStatus _detail13Label;
    [ObservableProperty] private string _detail13Value = "—";
    [ObservableProperty] private LocalizedStatus _detail14Label;
    [ObservableProperty] private string _detail14Value = "—";

    public void ClearHistory() => History.Clear();
    public void Update(double value, double maximum, string metric, string sideDetail, string l1, string v1, string l2, string v2, string l3, string v3, string l4, string v4)
    {
        Metric = metric; SideDetail = sideDetail; ChartMaximum = Math.Max(maximum, 1);
        Detail1Label = l1; Detail1Value = v1; Detail2Label = l2; Detail2Value = v2; Detail3Label = l3; Detail3Value = v3; Detail4Label = l4; Detail4Value = v4;
        History.Add(Math.Max(0, value));
        while (History.Count > 60) History.RemoveAt(0);
    }

    public void SetAdditionalDetails(params (string Label, string Value)[] details)
    {
        var values = details.Concat(Enumerable.Repeat((Label: string.Empty, Value: "—"), 10)).ToArray();
        Detail5Label = values[0].Label; Detail5Value = values[0].Value;
        Detail6Label = values[1].Label; Detail6Value = values[1].Value;
        Detail7Label = values[2].Label; Detail7Value = values[2].Value;
        Detail8Label = values[3].Label; Detail8Value = values[3].Value;
        Detail9Label = values[4].Label; Detail9Value = values[4].Value;
        Detail10Label = values[5].Label; Detail10Value = values[5].Value;
        Detail11Label = values[6].Label; Detail11Value = values[6].Value;
        Detail12Label = values[7].Label; Detail12Value = values[7].Value;
        Detail13Label = values[8].Label; Detail13Value = values[8].Value;
        Detail14Label = values[9].Label; Detail14Value = values[9].Value;
    }
}
