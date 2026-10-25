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
            new ShellDesktopEntry("app", "App shortcut", ShellDesktopEntryKind.Application, "A", Availability: new("Missing", "Needs components", false)),
            new ShellDesktopEntry("file", "Document.txt", ShellDesktopEntryKind.File, "F"),
        };
        state.PublishDesktop(new ShellDesktopState([], entries, true, Wallpaper: Avalonia.Media.Brushes.Black));
        var actions = DispatchProxy.Create<IShellActions, DesktopClipboardProxy>();
        ((DesktopClipboardProxy)(object)actions).Handler = (method, args) =>
        {
            if (method.Name == nameof(IShellActions.SetDesktopSelection))
            {
                var selected = (IReadOnlyList<string>)args![0]!;
                state.PublishDesktop(state.Desktop! with { DesktopEntries = state.Desktop.DesktopEntries.Select(entry => entry with { IsSelected = selected.Contains(entry.Id) }).ToArray() });
            }
            if (method.Name == nameof(IShellActions.ClearDesktopSelection))
                state.PublishDesktop(state.Desktop! with { DesktopEntries = state.Desktop.DesktopEntries.Select(entry => entry with { IsSelected = false }).ToArray() });
            return method.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        };
        Windows11ShellViewModel? activeModel = null;
        var overlayCalls = 0;
        var overlays = DispatchProxy.Create<IShellOverlayService, DesktopClipboardProxy>();
        ((DesktopClipboardProxy)(object)overlays).Handler = (_, _) =>
        {
            Check(activeModel is { IsStartOpen: false, IsQuickSettingsOpen: false }, "flyouts close before opening a desktop dialog.");
            overlayCalls++;
            return Task.CompletedTask;
        };
        var previousHandler = ((DesktopClipboardProxy)(object)actions).Handler;
        var actionCalls = 0;
        ((DesktopClipboardProxy)(object)actions).Handler = (method, values) =>
        {
            if (method.Name is nameof(IShellActions.OpenSettings) or nameof(IShellActions.ShowDesktop) or nameof(IShellActions.RefreshDesktopAsync) or nameof(IShellActions.LaunchAsync))
            {
                Check(activeModel is { IsStartOpen: false, IsQuickSettingsOpen: false }, "flyouts close before forwarding " + method.Name + ".");
                actionCalls++;
            }
            return previousHandler(method, values);
        };
        var language = DispatchProxy.Create<ILocalizationSnapshot, DesktopClipboardProxy>();
        ((DesktopClipboardProxy)(object)language).Handler = (method, args) => method.Name == "get_Language" ? "en-US" : method.Name == "Get" ? args![1] : null;
        using var localizer = new ShellLocalizer(language);
        using var vm = new Windows11ShellViewModel(new ShellPresentationContext(state, actions, overlays, new SelectionSurfaces(), language), localizer);
        activeModel = vm;
        vm.Activate();
        var view = new Windows11ShellView { DataContext = vm };
        var host = new Window { Width = 1000, Height = 700, Content = view };
        host.Show(); host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var terminal = new ShellApplicationEntry(new RelaxKonOS.Core.Applications.AppId("test.terminal"), "终端", "T", null, Availability: new("Missing", "Needs components", false));
        state.PublishDesktop(state.Desktop! with { Applications = [terminal, new ShellApplicationEntry(new RelaxKonOS.Core.Applications.AppId("test.editor"), "Editor", "E", null)] });
        vm.ToggleStartCommand.Execute(null);
        host.UpdateLayout();
        var search = view.GetVisualDescendants().OfType<TextBox>().First(text => text.IsEffectivelyVisible);
        search.Text = "终端";
        Check(vm.SearchQuery == "终端" && vm.IsApplicationListOpen && !vm.IsPinnedOpen && vm.FilteredApplications.SequenceEqual(new[] { terminal }), "typing in Start searches localized application names.");
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Check(view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Needs components" && text.IsEffectivelyVisible),
            "application tiles render the host-provided readiness label separately from the name.");
        var previewDirectory = Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "readiness");
        Directory.CreateDirectory(previewDirectory);
        using (var frame = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1000, 700)))
        {
            frame.Render(host);
            frame.Save(Path.Combine(previewDirectory, "external-windows11.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        search.Text = "TERMINAL";
        Check(vm.FilteredApplications.SequenceEqual(new[] { terminal }), "search matches application IDs without case sensitivity.");
        search.Text = "missing";
        Check(vm.FilteredApplications.Count == 0 && !vm.IsPinnedOpen, "unmatched search does not show unrelated pinned actions.");
        state.PublishDesktop(state.Desktop! with { Applications = [terminal] });
        Check(vm.FilteredApplications.Count == 0, "catalog refresh preserves the active search filter.");
        vm.CloseFlyoutsCommand.Execute(null);
        Check(vm.SearchQuery.Length == 0 && vm.IsPinnedOpen, "closing Start resets its search.");
        state.PublishDesktop(state.Desktop! with { DesktopEntries = state.Desktop!.DesktopEntries.Select(entry => entry.Id == "app"
            ? entry with { Availability = new("Stopped", "Stopped", true) } : entry).ToArray() });
        Check(vm.DesktopEntries.Single(entry => entry.Id == "app").Availability is { State: "Stopped", IsReady: true },
            "desktop dependency badges update when host availability changes.");
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
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
        var before = Icons().Select(button => (button, point: button.TranslatePoint(default, host), button.Bounds)).ToArray();
        vm.SetDesktopSelection(many.Take(12).Select(entry => entry.Id));
        host.UpdateLayout();
        foreach (var entry in before)
            Check(entry.button.Bounds == entry.Bounds && entry.button.TranslatePoint(default, host) == entry.point, "multi-column shortcut positions do not change on selection.");
        var selectedEntry = vm.DesktopEntries.First(entry => entry.IsSelected);
        var batchMenu = (ContextMenu)typeof(DesktopShortcutsView).GetMethod("CreateContextMenu", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [vm, selectedEntry])!;
        Check(batchMenu.ItemsSource!.OfType<MenuItem>().Count() == 1, "multi-shortcut context menu exposes only the shared delete action.");

        foreach (var command in new[] { vm.OpenSettingsCommand, vm.OpenPersonalizationCommand, vm.RefreshDesktopCommand, vm.ShowDesktopCommand })
        {
            vm.ToggleStartCommand.Execute(null);
            host.UpdateLayout();
            using var frame = host.CaptureRenderedFrame();
            var tile = view.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("start-tile") && ReferenceEquals(button.Command, command));
            var point = tile.TranslatePoint(new Point(30, 30), host)!.Value;
            host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
            Check(!vm.IsStartOpen, "clicking a pinned action dismisses Start.");
        }
        Check(actionCalls == 4, "each pinned action reaches its host command exactly once.");
        vm.ToggleQuickSettingsCommand.Execute(null);
        vm.OpenSettingsCommand.Execute(null);
        Check(!vm.IsQuickSettingsOpen, "Settings dismisses Quick Settings.");
        vm.ToggleStartCommand.Execute(null);
        vm.OpenDisplaySettingsCommand.Execute(null);
        Check(overlayCalls == 1 && !vm.IsStartOpen, "display settings dismisses Start before opening its dialog.");
        vm.ToggleStartCommand.Execute(null);
        vm.OpenApplicationCommand.Execute(new ShellApplicationEntry(new RelaxKonOS.Core.Applications.AppId("test.launch"), "Application", "A", null));
        vm.ToggleStartCommand.Execute(null);
        host.UpdateLayout();
        view.GetVisualDescendants().OfType<TextBox>().First(text => text.IsEffectivelyVisible).Focus();
        host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); host.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Check(!vm.IsStartOpen, "Escape dismisses Start.");
        var taskbarId = new RelaxKonOS.Core.Applications.AppId("test.pinned");
        state.PublishDesktop(state.Desktop! with { TaskbarEntries = [new ShellTaskbarEntry(taskbarId, "Pinned application", "P", null, true, false, false)] });
        host.UpdateLayout();
        using var taskbarFrame = host.CaptureRenderedFrame();
        var taskbarButton = view.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is ShellTaskbarEntry);
        Check(taskbarButton.IsEffectivelyVisible, "a pinned closed application is rendered in the taskbar.");
        var forwarded = 0;
        var beforeTaskbarHandler = ((DesktopClipboardProxy)(object)actions).Handler;
        ((DesktopClipboardProxy)(object)actions).Handler = (method, values) =>
        {
            if (method.Name == nameof(IShellActions.ToggleWindowGroup)) { forwarded++; return null; }
            if (method.Name == nameof(IShellActions.SetTaskbarPinnedAsync))
            {
                state.PublishDesktop(state.Desktop! with { TaskbarEntries = [] });
                return Task.CompletedTask;
            }
            return beforeTaskbarHandler(method, values);
        };
        var taskbarPoint = taskbarButton.TranslatePoint(new Point(20, 20), host)!.Value;
        host.MouseDown(taskbarPoint, MouseButton.Left); host.MouseUp(taskbarPoint, MouseButton.Left);
        Check(forwarded == 1, "clicking a pinned application forwards activation to the host.");
        vm.ToggleTaskbarPinCommand.Execute(vm.TaskbarEntries.Single());
        Check(vm.TaskbarEntries.Count == 0, "unpinning a closed application removes the external taskbar entry.");
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        manager.Attach(view.WindowHostSurface);
        manager.AttachFullScreenHost(view.FullScreenHostSurface);
        manager.SetHostBounds(new RelaxKonOS.Core.Primitives.Rect(0, 0, 1000, 650));
        manager.SetFullScreenHostBounds(new RelaxKonOS.Core.Primitives.Rect(0, 0, 1000, 700));
        var owner = manager.Create(new RelaxKonOS.WindowManager.WindowCreateOptions(new RelaxKonOS.Core.Applications.AppId("test.external.owner"), "Owner", new TextBox { Text = "editable" }));
        var shellInput = new TextBox { Text = "shell dialog text", Margin = new Thickness(20) };
        RelaxKonOS.WindowManager.ModalDialog<bool>? shellModal = null;
        var shellResult = manager.ShowShellDialogAsync<bool>("Shell dialog", handle => { shellModal = handle; return shellInput; }, new RelaxKonOS.Core.Primitives.Size(400, 300));
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using var shellFrame = host.CaptureRenderedFrame();
        var shellPoint = shellInput.TranslatePoint(new Point(30, 15), host)!.Value;
        var shellHit = host.InputHitTest(shellPoint) as Visual;
        Check(shellHit is not null && (ReferenceEquals(shellHit, shellInput) || shellHit.GetVisualAncestors().Contains(shellInput)), "shell dialog is above the desktop blocker.");
        host.MouseDown(shellPoint, MouseButton.Left); host.MouseUp(shellPoint, MouseButton.Left);
        Check(shellInput.IsKeyboardFocusWithin, "shell dialog field keeps focus after a pointer click.");
        shellModal!.Close(true);
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return shellResult.IsCompleted && !manager.IsSystemModalOpen; }, 2000);
        Check(!manager.IsSystemModalOpen, "closed shell dialog removes its modal session.");
        var input = new TextBox { Text = "dialog text", Margin = new Thickness(20) };
        RelaxKonOS.WindowManager.ModalDialog<bool>? modal = null;
        var result = manager.ShowSystemDialogAsync<bool>("System dialog", handle => { modal = handle; return input; }, new RelaxKonOS.Core.Primitives.Size(400, 300));
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using var dialogFrame = host.CaptureRenderedFrame();
        var dialogPoint = input.TranslatePoint(new Point(30, 15), host)!.Value;
        var hit = host.InputHitTest(dialogPoint) as Visual;
        Check(hit is not null && (ReferenceEquals(hit, input) || hit.GetVisualAncestors().Contains(input)), "system dialog remains above its input blocker and receives pointer input.");
        host.MouseDown(dialogPoint, MouseButton.Left); host.MouseUp(dialogPoint, MouseButton.Left);
        Check(input.IsKeyboardFocusWithin, "system dialog text field can receive focus.");
        modal!.Close(true);
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return result.IsCompleted && !manager.IsSystemModalOpen; }, 2000);
        manager.ForceClose(owner);
        manager.Detach();
        host.Close();
        return Task.CompletedTask;
    }
}
