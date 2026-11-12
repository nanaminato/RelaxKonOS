using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Protocol.Files;

internal static class ExplorerAddressChecks
{
    public static void Run(Window host)
    {
        using var vm = new ExplorerViewModel(DispatchProxy.Create<IExplorerClient, ExplorerAddressClient>());
        vm.NavigateToAsync("/home/user").GetAwaiter().GetResult();
        var view = new ExplorerMainView { DataContext = vm };
        host.Content = view;
        Pump();
        var frame = view.FindControl<Border>("AddressBarFrame")!;
        var editor = view.FindControl<TextBox>("AddressBox")!;
        void ClickBlank(MouseButton button = MouseButton.Left)
        {
            var point = frame.TranslatePoint(new Point(frame.Bounds.Width - 55, 16), host)!.Value;
            host.MouseDown(point, button);
            host.MouseUp(point, button);
            Pump();
        }
        ClickBlank(MouseButton.Right);
        Check(!vm.IsEditingAddress, "Right-click does not start address editing");
        ClickBlank();
        Check(vm.IsEditingAddress && editor.IsFocused, "Blank address space opens and focuses editor");
        Check(editor.SelectedText == "/home/user", "Current path is selected for replacement");
        editor.Text = "/draft";
        view.FocusAddressBox();
        Check(editor.Text == "/draft", "Repeated address shortcut preserves draft");
        host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Check(!vm.IsEditingAddress && vm.AddressInput == "/home/user", "Escape cancels without navigation");
        ClickBlank();
        editor.Text = "/next";
        host.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Pump();
        Check(vm.AddressbarPath == "/next" && !vm.IsEditingAddress, "Enter navigates and restores breadcrumbs");
        var crumb = frame.GetVisualDescendants().OfType<Button>().First(b => b.Tag as string == "/");
        var crumbPoint = crumb.TranslatePoint(new Point(5, 5), host)!.Value;
        host.MouseDown(crumbPoint, MouseButton.Left);
        host.MouseUp(crumbPoint, MouseButton.Left);
        Pump();
        Check(vm.AddressbarPath == "/" && !vm.IsEditingAddress, "Breadcrumb click navigates instead of editing");
        ClickBlank();
        editor.Text = "/discard";
        view.FocusSearchBox();
        Pump();
        Check(!vm.IsEditingAddress && vm.AddressInput == "/", "Leaving editor discards unsubmitted path");
        host.Content = null;
        Console.WriteLine("Explorer address bar: 8 interaction checks passed.");
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class ExplorerAddressClient : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(IExplorerClient.GetDirectoryAsync) => Task.FromResult(new DirectoryDto((string)args![0]!, (string)args[0]!, FileSystemEntryType.Directory, [], [], null, null)),
        nameof(IExplorerClient.GetDrivesAsync) => Task.FromResult<IReadOnlyList<DriveDto>>([]),
        nameof(IExplorerClient.GetSpecialLocationsAsync) => Task.FromResult<IReadOnlyList<SpecialLocationDto>>([]),
        _ => throw new NotSupportedException(method?.Name)
    };
}
