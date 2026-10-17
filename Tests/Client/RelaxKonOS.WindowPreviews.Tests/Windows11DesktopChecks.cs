using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Example.Windows11DesktopShell.Services;
using RelaxKonOS.Example.Windows11DesktopShell.ViewModels;
using RelaxKonOS.Example.Windows11DesktopShell.Views;
using RelaxKonOS.Shell;

internal static class Windows11DesktopChecks
{
    public static Task RunAsync()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: Windows 11: " + message); }
        var state = new ShellStateStore();
        var entries = new[]
        {
            new ShellDesktopEntry("app", "App shortcut", ShellDesktopEntryKind.Application, "A"),
            new ShellDesktopEntry("file", "Document.txt", ShellDesktopEntryKind.File, "F"),
        };
        state.PublishDesktop(new ShellDesktopState([], entries, true, Wallpaper: Avalonia.Media.Brushes.Black));
        var actions = DispatchProxy.Create<IShellActions, DesktopClipboardProxy>();
        ((DesktopClipboardProxy)(object)actions).Handler = (method, args) =>
        {
            if (method.Name == nameof(IShellActions.SetDesktopSelection))
            {
                var selected = (IReadOnlyList<string>)args![0]!;
                state.PublishDesktop(state.Desktop! with { DesktopEntries = entries.Select(entry => entry with { IsSelected = selected.Contains(entry.Id) }).ToArray() });
            }
            if (method.Name == nameof(IShellActions.ClearDesktopSelection))
                state.PublishDesktop(state.Desktop! with { DesktopEntries = entries.Select(entry => entry with { IsSelected = false }).ToArray() });
            return method.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        };
        var language = DispatchProxy.Create<ILocalizationSnapshot, DesktopClipboardProxy>();
        ((DesktopClipboardProxy)(object)language).Handler = (method, args) => method.Name == "get_Language" ? "en-US" : method.Name == "Get" ? args![1] : null;
        using var localizer = new ShellLocalizer(language);
        using var vm = new Windows11ShellViewModel(new ShellPresentationContext(state, actions, null!, new SelectionSurfaces(), language), localizer);
        typeof(Windows11ShellViewModel).GetProperty(nameof(vm.IsLoading))!.SetValue(vm, false);
        var view = new Windows11ShellView { DataContext = vm };
        var host = new Window { Width = 1000, Height = 700, Content = view };
        host.Show(); host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Button[] Icons() => view.GetVisualDescendants().OfType<Button>().Where(button => button.DataContext is ShellDesktopEntry).ToArray();
        var buttons = Icons();
        Check(buttons.Length == 2, "application shortcut and file render together.");
        var start = buttons[0].TranslatePoint(new Point(-5, -5), host)!.Value;
        var end = buttons[1].TranslatePoint(new Point(103, 105), host)!.Value;
        host.MouseDown(start, MouseButton.Left); host.MouseMove(end); host.MouseUp(end, MouseButton.Left);
        Check(vm.DesktopEntries.All(entry => entry.IsSelected), "marquee selects shortcuts and files.");
        Check(ReferenceEquals(buttons[0], Icons()[0]), "selection publication preserves icon controls.");
        var center = buttons[0].TranslatePoint(new Point(40, 40), host)!.Value;
        host.MouseDown(center, MouseButton.Left, RawInputModifiers.Control); host.MouseUp(center, MouseButton.Left, RawInputModifiers.Control);
        Check(!vm.DesktopEntries[0].IsSelected && vm.DesktopEntries[1].IsSelected, "Ctrl-click toggles without clearing another selection.");
        host.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null); host.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
        Check(vm.DesktopEntries.All(entry => entry.IsSelected), "Ctrl+A works from the desktop.");
        host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); host.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Check(vm.DesktopEntries.All(entry => !entry.IsSelected), "Escape clears selection.");
        Check(vm.DesktopEntries[0].IsShortcut && !vm.DesktopEntries[1].IsShortcut, "shortcut badges exclude real files.");
        host.MouseDown(end, MouseButton.Left); host.MouseMove(start); host.MouseUp(start, MouseButton.Left);
        Check(vm.DesktopEntries.All(entry => entry.IsSelected), "reverse marquee selects the same mixed entries.");
        host.MouseDown(start, MouseButton.Left); host.MouseMove(end);
        host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); host.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        host.MouseUp(end, MouseButton.Left);
        Check(vm.DesktopEntries.All(entry => !entry.IsSelected), "Escape cancels an active marquee and releases its pointer capture.");
        var many = Enumerable.Range(0, 20).Select(index => new ShellDesktopEntry(index.ToString(), "Shortcut " + index, ShellDesktopEntryKind.Shortcut, "S")).ToArray();
        state.PublishDesktop(state.Desktop! with { DesktopEntries = many });
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var columns = Icons().Select(button => button.TranslatePoint(default, host)!.Value.X).Distinct().Count();
        Check(columns > 1, "desktop grid wraps into additional columns within the available height.");
        host.Close();
        return Task.CompletedTask;
    }
}
