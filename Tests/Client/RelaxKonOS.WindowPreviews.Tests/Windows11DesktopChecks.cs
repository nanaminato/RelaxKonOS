using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
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
        // The desktop menu belongs to the wallpaper surface. Hanging it on the desktop area instead
        // made every right click inside a managed window answer with the wallpaper menu.
        var desktopBackdrop = (Border)view.DesktopBackdropSurface;
        Check(desktopBackdrop.ContextMenu is not null, "the wallpaper surface owns the desktop context menu.");
        Check(!view.WindowHostSurface.GetVisualAncestors().Contains(desktopBackdrop),
            "the desktop context menu surface sits outside the window host.");
        var desktopMenusOpened = 0;
        desktopBackdrop.ContextMenu!.Opening += (_, _) => desktopMenusOpened++;
        var probeContent = new TextBlock { Text = "content", Margin = new Thickness(24) };
        var menuProbe = manager.Create(new RelaxKonOS.WindowManager.WindowCreateOptions(
            new RelaxKonOS.Core.Applications.AppId("test.window.menu"), "Window menu", probeContent));
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var titleBar = menuProbe.View.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_TitleBar");
        var windowMenu = titleBar.ContextMenu;
        Check(windowMenu is not null, "the title bar offers the window menu.");
        var contentPoint = menuProbe.View.TranslatePoint(new Point(400, 300), host)!.Value;
        host.MouseDown(contentPoint, MouseButton.Right); host.MouseUp(contentPoint, MouseButton.Right);
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Check(desktopMenusOpened == 0, "right-clicking a managed window never opens the desktop menu.");
        Check(!windowMenu!.IsOpen, "the window menu belongs to the title bar, not to the window content.");
        // A shell that owns its menu on an ancestor of the window host still cannot answer a window
        // right click: the window ends the request before the shell sees it.
        var shellSawRequest = 0;
        ((Control)view.WindowHostSurface.GetVisualParent()!)
            .AddHandler(InputElement.ContextRequestedEvent, (_, _) => shellSawRequest++, RoutingStrategies.Bubble);
        host.MouseDown(contentPoint, MouseButton.Right); host.MouseUp(contentPoint, MouseButton.Right);
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Check(shellSawRequest == 0, "an unclaimed context request stops at the window instead of reaching the shell.");
        var titlePoint = menuProbe.View.TranslatePoint(new Point(200, 15), host)!.Value;
        host.MouseDown(titlePoint, MouseButton.Right); host.MouseUp(titlePoint, MouseButton.Right);
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Check(windowMenu.IsOpen, "right-clicking a title bar opens the window menu.");
        var windowActions = windowMenu.ItemsSource!.OfType<MenuItem>().ToArray();
        Check(windowActions.Length == 4 && ReferenceEquals(windowActions[^1].Command, menuProbe.CloseCommand) &&
              windowActions[0].Header as string == RelaxKonOS.WindowManager.WindowCaptionLabels.Current.Restore,
            "the window menu lists restore, maximize, minimize and close in the host's language.");
        windowMenu.Close();
        // Alt+Space is the pointer-free route to the same menu. A keyboard open has no pointer to
        // anchor to, so the host places it under the caption instead.
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        manager.Focus(menuProbe);
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        host.KeyPress(Key.Space, RawInputModifiers.Alt, PhysicalKey.Space, null);
        host.KeyRelease(Key.Space, RawInputModifiers.Alt, PhysicalKey.Space, null);
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Check(windowMenu.IsOpen && windowMenu.Placement == PlacementMode.BottomEdgeAlignedLeft,
            "Alt+Space opens the window menu under the title bar.");
        windowMenu.Close();
        // An application's own menu inside the window keeps working: the boundary only stops
        // requests nobody claimed.
        var appContent = new Border { Background = Avalonia.Media.Brushes.DimGray, Margin = new Thickness(24) };
        var appProbe = manager.Create(new RelaxKonOS.WindowManager.WindowCreateOptions(
            new RelaxKonOS.Core.Applications.AppId("test.window.app.menu"), "Application menu", appContent));
        var appMenuOpened = 0;
        var appMenu = new ContextMenu { ItemsSource = new object[] { new MenuItem { Header = "Application action" } } };
        appMenu.Opening += (_, _) => appMenuOpened++;
        appContent.ContextMenu = appMenu;
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var appPoint = appContent.TranslatePoint(new Point(8, 8), host)!.Value;
        host.MouseDown(appPoint, MouseButton.Right); host.MouseUp(appPoint, MouseButton.Right);
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Check(appMenuOpened == 1 && desktopMenusOpened == 0,
            "an application menu inside a window still opens and stays inside the window.");
        appMenu.Close();
        manager.ForceClose(appProbe);
        manager.ForceClose(menuProbe);
        manager.ForceClose(owner);
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var wallpaperPoint = desktopBackdrop.TranslatePoint(new Point(600, 320), host)!.Value;
        host.MouseDown(wallpaperPoint, MouseButton.Right); host.MouseUp(wallpaperPoint, MouseButton.Right);
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Check(desktopMenusOpened == 1, "right-clicking empty wallpaper still opens the desktop menu.");
        desktopBackdrop.ContextMenu.Close();
        manager.Detach();
        host.Close();
        return Task.CompletedTask;
    }
}
