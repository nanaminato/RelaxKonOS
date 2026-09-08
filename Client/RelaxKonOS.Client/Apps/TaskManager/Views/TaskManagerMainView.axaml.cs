using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Apps.TaskManager.ViewModels;

namespace RelaxKonOS.Client.Apps.TaskManager.Views;

/// <summary>TaskManagerMainView 的 code-behind。处理过滤框按键（Esc 清除）与卸载时停止定时刷新。
/// 主要逻辑在 <see cref="TaskManagerViewModel"/>；此处仅做轻量 UI 事件桥接。</summary>
public partial class TaskManagerMainView : UserControl
{
    public TaskManagerMainView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>Moves keyboard focus to the process filter field.</summary>
    public void FocusProcessFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private TaskManagerViewModel? ViewModel => DataContext as TaskManagerViewModel;

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
