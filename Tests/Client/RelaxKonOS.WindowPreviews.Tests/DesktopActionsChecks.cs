using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Protocol.Files;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.VirtualSystemDrive;
using RelaxKonOS.Runtime;
using RelaxKonOS.Shell;

internal static class DesktopActionsChecks
{
    public static async Task RunAsync(ShellSettings settings, LocalizationService localization, IServiceProvider services)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: Desktop actions: " + message); }
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        manager.Attach(new Canvas { Width = 1000, Height = 700 });
        var applications = new ApplicationManager(manager, services);
        var firstApp = new ActionApplication("actions.first");
        var secondApp = new ActionApplication("actions.second");
        applications.Register(firstApp); applications.Register(secondApp);
        var drive = new VirtualSystemDrive();
        typeof(VirtualSystemDrive).GetField("<Root>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(drive, Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "desktop-actions", Guid.NewGuid().ToString("N")));
        var shortcuts = new ShortcutStore(drive);
        var link = await shortcuts.CreateAsync(new RelaxKonOSShortcut(1, Guid.NewGuid().ToString(), "Link", RelaxKonOSShortcutKind.Application, firstApp.Manifest.Id.Value));
        var deleted = new List<string>();
        var files = DispatchProxy.Create<IExplorerClient, DesktopClipboardProxy>();
        ((DesktopClipboardProxy)(object)files).Handler = (method, values) =>
        {
            if (method.Name == nameof(IExplorerClient.DeleteAsync)) { deleted.Add((string)values![0]!); return Task.CompletedTask; }
            throw new Exception("Unexpected file call: " + method.Name);
        };
        var vm = new DesktopShellViewModel(manager, applications, settings, localization,
            DispatchProxy.Create<IAuthSession, UnusedPreviewServices>(), new SshDesktopSession(null!, new ShellSettings(null!)), () => { },
            null!, files, null!, null!, null!, null!, null!, null!, null!, shortcuts, null!, null!);
        vm.PopulateDesktop(); Dispatcher.UIThread.RunJobs();
        var first = vm.DesktopIcons.Single(app => app.Id == firstApp.Manifest.Id);
        var second = vm.DesktopIcons.Single(app => app.Id == secondApp.Manifest.Id);
        var state = new ShellStateStore(); state.Publish(vm);
        using var adapter = new DesktopShellStateAdapter(vm, state, new EmptyShellCatalog());
        state.PublishDesktop(adapter.Current);
        var shell = new WindowsLikeDesktopShell();
        var surfaces = new PreviewSurfaceRegistry(manager);
        await shell.InitializeAsync(new ShellPresentationContext(state, null!, null!, surfaces, null!), CancellationToken.None);
        manager.Detach(); manager.Attach(surfaces.Surfaces!.WindowHost); manager.AttachFullScreenHost(surfaces.Surfaces.FullScreenWindowHost);
        var host = new Window { Width = 1000, Height = 700, Content = shell.View };
        host.Show(); host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var buttons = shell.View.GetVisualDescendants().OfType<Button>().Where(button => button.DataContext is AppEntryViewModel).ToArray();
        var hoverPoint = buttons[0].TranslatePoint(new Point(40, 40), host)!.Value;
        host.MouseMove(hoverPoint);
        using var hoverFrame = host.CaptureRenderedFrame();
        var presenter = buttons[0].GetVisualDescendants().OfType<ContentPresenter>().First();
        Check(presenter.Background is ISolidColorBrush hover && hover.Color.A is > 0 and < 128,
            "desktop hover uses a translucent Windows-style surface instead of an opaque application button background.");
        var geometry = buttons.Select(button => (button, button.Bounds, point: button.TranslatePoint(default, host))).ToArray();
        for (var round = 0; round < 10; round++)
        {
            vm.SetDesktopSelection([first, second]); host.UpdateLayout();
            foreach (var entry in geometry)
                Check(entry.button.Bounds == entry.Bounds && entry.button.TranslatePoint(default, host) == entry.point, "selection keeps application icon geometry fixed.");
            vm.ClearDesktopSelectionCommand.Execute(null); host.UpdateLayout();
        }
        vm.SetDesktopSelection([first, second]);
        var menu = (ContextMenu)typeof(LauncherDesktopShellBase).GetMethod("CreateDesktopItemContextMenu", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [vm, first])!;
        Check(menu.ItemsSource!.OfType<MenuItem>().Count() == 1 && ReferenceEquals(menu.ItemsSource!.OfType<MenuItem>().Single().Command, vm.DeleteDesktopItemsCommand), "multi-application menu hides single-item actions.");
        await vm.ToggleTaskbarPinCommand.ExecuteAsync(first.Id);
        Dispatcher.UIThread.RunJobs();
        Check(vm.TaskbarGroups.Single().IsPinned && vm.TaskbarGroups.Single().WindowCount == 0, "pinning creates a taskbar entry before launch.");
        Check(adapter.Current.TaskbarEntries!.Single().IsPinned && !adapter.Current.TaskbarEntries!.Single().IsRunning,
            "the external-shell projection receives a pinned closed application.");
        vm.ToggleTaskbarGroupCommand.Execute(vm.TaskbarGroups.Single());
        Check(firstApp.Activations == 1 && vm.TaskbarGroups.Single().WindowCount == 1, "clicking a pinned closed application launches it.");
        Check(adapter.Current.TaskbarEntries!.Single().IsRunning, "the external-shell projection follows launched windows.");
        manager.Close(manager.Windows.Single());
        Check(vm.TaskbarGroups.Single().WindowCount == 0 && vm.TaskbarGroups.Single().HasIconImage, "closing the last window retains the pinned application and artwork.");
        var snapshot = settings.ToPreferences();
        settings.Apply(snapshot);
        Check(settings.PinnedAppIds.SequenceEqual(new[] { first.Id.Value }), "taskbar pins round-trip through workspace preferences.");
        vm.SelectDesktopItemCommand.Execute(second);
        await vm.DeleteDesktopItemsCommand.ExecuteAsync(second);
        Dispatcher.UIThread.RunJobs();
        Check(settings.ShowBuiltInApps && settings.VisibleAppIds.SequenceEqual(new[] { first.Id.Value }) && vm.DesktopIcons.Count == 1,
            "deleting one application shortcut switches all-app visibility to the remaining explicit selection.");
        vm.SetDesktopSelection([first]);
        await vm.DeleteDesktopItemsCommand.ExecuteAsync(first);
        Dispatcher.UIThread.RunJobs();
        Check(!settings.ShowBuiltInApps && vm.DesktopIcons.Count == 0, "deleting the last selected application shortcuts does not restore all apps.");
        Check(applications.Registered.Count == 2 && settings.PinnedAppIds.Contains(first.Id.Value), "shortcut removal preserves installed applications and taskbar pins.");
        var editType = typeof(DesktopDisplayDialogs).Assembly.GetType("RelaxKonOS.Client.Views.Shell.DesktopDisplayEditBuffer")!;
        var buffer = Activator.CreateInstance(editType, settings, applications)!;
        Check(!(bool)editType.GetProperty("ShowBuiltInApps")!.GetValue(buffer)!, "desktop display configuration reflects shortcut removal.");
        var shortcutVm = new ShortcutEntryViewModel(link, null!);
        vm.DesktopShortcuts.Add(shortcutVm); vm.DesktopItems.Add(shortcutVm);
        await vm.DeleteDesktopItemsCommand.ExecuteAsync(shortcutVm);
        Check(!(await shortcuts.ListAsync()).Any(item => item.Id == link.Id), "deleting a shortcut removes only its persisted link.");
        await vm.ToggleTaskbarPinCommand.ExecuteAsync(first.Id);
        Dispatcher.UIThread.RunJobs();
        Check(vm.TaskbarGroups.Count == 0 && settings.PinnedAppIds.Count == 0, "unpinning a closed application removes its taskbar entry.");
        var firstFile = new DesktopFileEntryViewModel(new FileSystemEntryDto("/desktop/a.txt", "a.txt", 1, FileSystemEntryType.File, null, null, null, false, false, null));
        var secondFile = new DesktopFileEntryViewModel(firstFile.Entry with { Path = "/desktop/b.txt", Name = "b.txt" });
        vm.DesktopFiles.Add(firstFile); vm.DesktopFiles.Add(secondFile);
        vm.DesktopItems.Add(firstFile); vm.DesktopItems.Add(secondFile);
        vm.DesktopItems.Add(first);
        vm.SetDesktopSelection([first, firstFile]);
        var publications = 0;
        vm.PublishRemoteFilesAsync = _ => { publications++; return Task.CompletedTask; };
        await vm.CopyDesktopEntryCommand.ExecuteAsync(firstFile);
        Check(publications == 0, "mixed shortcut/file selection does not silently copy only its files.");
        vm.DesktopItems.Remove(first);
        vm.SetDesktopSelection([firstFile, secondFile]);
        var prompts = 0;
        vm.RequestDesktopConfirmAsync = (_, _, _) => { prompts++; return Task.FromResult(false); };
        await vm.DeleteDesktopItemsCommand.ExecuteAsync(firstFile);
        Check(deleted.Count == 0 && prompts == 1, "cancelling a file batch deletes no files.");
        vm.RequestDesktopConfirmAsync = (_, _, _) => { prompts++; return Task.FromResult(true); };
        await vm.DeleteDesktopItemsCommand.ExecuteAsync(firstFile);
        Check(deleted.SequenceEqual(new[] { firstFile.Entry.Path, secondFile.Entry.Path }) && prompts == 2, "confirmed multi-file deletion processes every selected file with one confirmation.");
        host.Close(); await shell.DisposeAsync();
    }

    private sealed class EmptyShellCatalog : IShellCatalog
    {
        public IReadOnlyList<RelaxKonOS.Shell.ShellDescriptor> Available => [];
        public event EventHandler? Changed { add { } remove { } }
        public bool TryGet(string id, out RelaxKonOS.Shell.ShellDescriptor descriptor) { descriptor = null!; return false; }
    }

    private sealed class ActionApplication(string id) : RemoteApplicationBase
    {
        public int Activations { get; private set; }
        public override ApplicationManifest Manifest { get; } = new(new AppId(id), id, IconGlyph: "A", IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/terminal.png");
        public override void Activate(RelaxKonOS.AppSDK.AppContext context) { Activations++; context.ShowWindow("Action application", new Border()); }
    }
}
