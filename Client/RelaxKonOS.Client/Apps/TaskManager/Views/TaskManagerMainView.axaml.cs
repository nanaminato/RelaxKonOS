using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.TaskManager.ViewModels;

namespace RelaxKonOS.Client.Apps.TaskManager.Views;

/// <summary>TaskManagerMainView 的 code-behind。处理过滤框按键（Esc 清除）与卸载时停止定时刷新。
/// 主要逻辑在 <see cref="TaskManagerViewModel"/>；此处仅做轻量 UI 事件桥接。</summary>
public partial class TaskManagerMainView : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        if (ReferenceEquals(window.View.HeaderCenter, HeaderCenter)) return;
        HeaderLeading.DataContext = HeaderCenter.DataContext = DataContext;
        HeaderHost.Children.Clear();
        window.View.HeaderLeading = HeaderLeading;
        window.View.HeaderCenter = HeaderCenter;
        window.View.HeaderTrailing = null;
        window.View.HeaderTabs = window.View.HeaderToolbar = null;
    }

    public TaskManagerMainView()
    {
        InitializeComponent();
        ProcessesGrid.AddHandler(PointerPressedEvent, SortHeaderPressed, RoutingStrategies.Tunnel);
        ProcessesGrid.AddHandler(PointerReleasedEvent, SortHeaderReleased, RoutingStrategies.Tunnel);
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => UpdateSortIndicator();
    }

    /// <summary>Moves keyboard focus to the process filter field.</summary>
    public void FocusProcessFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private TaskManagerViewModel? ViewModel => DataContext as TaskManagerViewModel;

    private void ProcessIcon_DataContextChanged(object? sender, EventArgs e) => LoadVisibleProcessIcon(sender);

    private void ProcessIcon_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => LoadVisibleProcessIcon(sender);

    private void LoadVisibleProcessIcon(object? sender)
    {
        if (sender is Image { DataContext: ProcessTreeItemViewModel row })
            _ = ViewModel?.LoadProcessIconAsync(row);
    }

    private DataGridColumnHeader? _pressedSortHeader;
    private Point _sortPressPosition;
    private double _sortPressWidth;

    private static DataGridColumnHeader? FindSortHeader(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is DataGridColumnHeader header) return header;
        return null;
    }

    private void SortHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedSortHeader = null;
        if (!e.GetCurrentPoint(ProcessesGrid).Properties.IsLeftButtonPressed) return;
        if (FindSortHeader(e.Source) is not { } header) return;
        var position = e.GetPosition(header);
        // Leave divider gestures entirely to the DataGrid's column resizer.
        if (position.X < 6 || position.X > header.Bounds.Width - 6) return;
        _pressedSortHeader = header;
        _sortPressWidth = header.Bounds.Width;
        _sortPressPosition = e.GetPosition(ProcessesGrid);
    }

    private void SortHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        var header = _pressedSortHeader;
        _pressedSortHeader = null;
        if (e.InitialPressMouseButton != MouseButton.Left || header is null
            || !ReferenceEquals(header, FindSortHeader(e.Source))) return;
        if (Math.Abs(header.Bounds.Width - _sortPressWidth) >= 0.1
            || Math.Abs(e.GetPosition(ProcessesGrid).X - _sortPressPosition.X) > 4
            || Math.Abs(e.GetPosition(ProcessesGrid).Y - _sortPressPosition.Y) > 4) return;
        var column = ProcessesGrid.Columns.FirstOrDefault(column => Equals(column.Header, header.Content));
        if (column is null || ViewModel is not { } vm) return;
        vm.SortProcesses(column.SortMemberPath);
        UpdateSortIndicator();
    }

    private void UpdateSortIndicator()
    {
        if (ViewModel is not { } vm) return;
        foreach (var column in ProcessesGrid.Columns)
        {
            var title = column.SortMemberPath switch
            {
                "Name" => Localization.LocalizedText.Get("common.name"),
                "MemoryBytes" => Localization.LocalizedText.Get("task_manager.memory"),
                "UserName" => Localization.LocalizedText.Get("task_manager.user"),
                "ThreadCount" => Localization.LocalizedText.Get("task_manager.threads"),
                "CpuPercent" => "CPU",
                _ => "PID",
            };
            column.Header = title + (column.SortMemberPath == vm.ProcessSortColumn
                ? vm.ProcessSortDescending ? " ▼" : " ▲" : string.Empty);
        }
    }

    private void FilterBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Esc 清除过滤词
        if (e.Key == Key.Escape && ViewModel is not null)
        {
            ViewModel.ProcessFilter = string.Empty;
            e.Handled = true;
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // 窗口关闭/卸载时停止定时器，避免对已关闭视图继续刷新
        ViewModel?.Stop();
    }
}
