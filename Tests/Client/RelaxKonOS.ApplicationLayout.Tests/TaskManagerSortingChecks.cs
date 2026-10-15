using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Apps.TaskManager.ViewModels;
using RelaxKonOS.Client.Apps.TaskManager.Views;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.SystemMonitor;

internal static class TaskManagerSortingChecks
{
    public static void Run(Window host)
    {
        var auth = DispatchProxy.Create<IAuthSession, SortingSession>();
        var vm = new TaskManagerViewModel(null!, new PerformanceStream(auth), null!, auth)
        { IsAutoRefresh = false, ActiveTab = TaskManagerTab.Processes };
        var processes = new List<ProcessInfoDto>
        {
            new(20, "beta", 2, 100, "alice", null, 8, null),
            new(10, "alpha", 9, 300, "zoe", null, 2, null),
            new(30, "gamma", 5, 200, "bob", null, 5, null),
        };
        typeof(TaskManagerViewModel).GetField("_allProcesses", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, processes);
        vm.SortProcesses("Name");
        var view = new TaskManagerMainView { DataContext = vm };
        host.Width = 1300;
        host.Content = view;
        Pump();
        var grid = view.FindControl<DataGrid>("ProcessesGrid")!;
        foreach (var column in grid.Columns)
        {
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                .Single(header => Equals(header.Content, column.Header));
            var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), host)!.Value;
            for (var click = 0; click < 2; click++)
            {
                var previousDirection = vm.ProcessSortDescending;
                var previousColumn = vm.ProcessSortColumn;
                host.MouseDown(point, MouseButton.Left);
                host.MouseUp(point, MouseButton.Left);
                Pump();
                Check(vm.ProcessSortColumn == column.SortMemberPath, $"Header click must select {column.SortMemberPath}.");
                if (previousColumn == vm.ProcessSortColumn)
                    Check(vm.ProcessSortDescending != previousDirection, "Repeated header click must reverse direction.");
                Func<ProcessInfoDto, IComparable> key = column.SortMemberPath switch
                {
                    "Name" => p => p.Name,
                    "Id" => p => p.Id,
                    "CpuPercent" => p => p.CpuPercent,
                    "MemoryBytes" => p => p.MemoryBytes,
                    "UserName" => p => p.UserName!,
                    _ => p => p.ThreadCount,
                };
                var expected = vm.ProcessSortDescending ? processes.OrderByDescending(key) : processes.OrderBy(key);
                Check(vm.FilteredProcesses.Select(row => row.Process.Id).SequenceEqual(expected.Select(p => p.Id)),
                    $"{column.SortMemberPath} must actually reorder the rows.");
            }
        }
        var resizeColumn = grid.Columns[1];
        var resizeHeader = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(header => Equals(header.Content, resizeColumn.Header));
        var oldWidth = resizeHeader.Bounds.Width;
        var sortColumn = vm.ProcessSortColumn;
        var descending = vm.ProcessSortDescending;
        var divider = resizeHeader.TranslatePoint(new Point(oldWidth - 1, resizeHeader.Bounds.Height / 2), host)!.Value;
        host.MouseDown(divider, MouseButton.Left);
        host.MouseMove(new Point(divider.X + 40, divider.Y));
        host.MouseUp(new Point(divider.X + 40, divider.Y), MouseButton.Left);
        Pump();
        Check(resizeHeader.Bounds.Width > oldWidth + 20, "Dragging the divider must resize its column.");
        Check(vm.ProcessSortColumn == sortColumn && vm.ProcessSortDescending == descending,
            "Resizing must not change sorting.");
        Console.WriteLine("PASS: Actual mouse clicks sort all six process columns in both directions; column resize does not trigger sorting.");
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class SortingSession : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
}
