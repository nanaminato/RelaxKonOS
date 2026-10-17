using RelaxKonOS.Runtime;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Shell;
using Avalonia.Input;
using Avalonia.Input.Raw;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Files;

internal static class DesktopClipboardChecks
{
    public static async Task RunAsync(ShellSettings settings, LocalizationService localization, IServiceProvider services)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        var session = DispatchProxy.Create<IAuthSession, DesktopClipboardProxy>();
        ((DesktopClipboardProxy)(object)session).Handler = (method, _) => method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        var diagnostics = DispatchProxy.Create<IAppActivationDiagnostics, DesktopClipboardProxy>();
        var files = DispatchProxy.Create<IExplorerClient, DesktopClipboardProxy>();
        var remoteCalls = 0;
        ((DesktopClipboardProxy)(object)files).Handler = (method, _) =>
        {
            if (method.Name == nameof(IExplorerClient.CopyAsync)) { remoteCalls++; return Task.FromException<FileSystemEntryDto>(new IOException("Stop after route assertion")); }
            throw new Exception("Unexpected desktop file call: " + method.Name);
        };
        var clipboard = new RemoteFileClipboard();
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        var shell = new DesktopShellViewModel(windows, new ApplicationManager(windows, services), settings, localization,
            session, new SshDesktopSession(null!, new ShellSettings(null!)), () => { },
            null!, files, clipboard, null!, null!, diagnostics, null!, null!, null!, null!, null!, null!);
        typeof(DesktopShellViewModel).GetField("_desktopPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, "/desktop");
        var entry = new FileSystemEntryDto("/source/a.bin", "a.bin", 10, FileSystemEntryType.File, null, null, null, false, false, null);
        var icon = new DesktopFileEntryViewModel(entry);
        IReadOnlyList<FileSystemEntryDto>? published = null;
        shell.PublishRemoteFilesAsync = entries => { published = entries; return Task.CompletedTask; };
        await shell.CopyDesktopEntryCommand.ExecuteAsync(icon);
        Check(published?.Single() == entry && clipboard.Operation == RemoteFileClipboardOperation.Copy,
            "Desktop copy publishes the selected file through the system clipboard bridge.");
        published = null;
        await shell.CutDesktopEntryCommand.ExecuteAsync(icon);
        Check(published?.Single() == entry && clipboard.Operation == RemoteFileClipboardOperation.Cut,
            "Desktop cut publishes virtual files and preserves the internal move intent.");
        shell.PublishRemoteFilesAsync = _ => Task.FromException(new IOException("Clipboard is busy"));
        var errors = 0;
        shell.ShowDesktopPasteErrorAsync = _ => { errors++; return Task.CompletedTask; };
        await shell.CopyDesktopEntryCommand.ExecuteAsync(icon);
        Check(errors == 1 && clipboard.Operation == RemoteFileClipboardOperation.Cut,
            "Failed system clipboard publication preserves the previous remote selection and reports the error.");
        var second = new DesktopFileEntryViewModel(entry with { Path = "/source/b.bin", Name = "b.bin" });
        shell.DesktopFiles.Add(icon);
        shell.DesktopFiles.Add(second);
        shell.DesktopItems.Add(icon);
        shell.DesktopItems.Add(second);
        shell.SetDesktopSelection([icon, second]);
        Check(icon.IsDesktopSelected && second.IsDesktopSelected, "Desktop selection accepts multiple files.");
        shell.ToggleDesktopSelection(icon);
        Check(!icon.IsDesktopSelected && second.IsDesktopSelected, "Modifier toggle preserves other selected entries.");
        shell.SetDesktopSelection([icon, second]);
        shell.PublishRemoteFilesAsync = entries => { published = entries; return Task.CompletedTask; };
        await shell.CopyDesktopEntryCommand.ExecuteAsync(icon);
        Check(published?.Count == 2 && clipboard.Entries.Count == 2, "Copy publishes every selected desktop file.");
        await shell.CutDesktopEntryCommand.ExecuteAsync(second);
        Check(published?.Count == 2 && clipboard.Operation == RemoteFileClipboardOperation.Cut, "Cut preserves the complete multi-file move selection.");
        shell.ClearDesktopSelectionCommand.Execute(null);
        Check(!icon.IsDesktopSelected && !second.IsDesktopSelected, "Clear selection clears all desktop files.");
        foreach (var presentation in new LauncherDesktopShellBase[] { new WindowsLikeDesktopShell(), new MacosLikeDesktopShell(), new UbuntuLikeDesktopShell() })
        {
            var state = new ShellStateStore();
            state.Publish(shell);
            var surfaces = new SelectionSurfaces();
            await presentation.InitializeAsync(new ShellPresentationContext(state, null!, null!, surfaces, null!), CancellationToken.None);
            var host = new Window { Width = 1000, Height = 700, Content = presentation.View };
            host.Show();
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var buttons = presentation.View.GetVisualDescendants().OfType<Button>().Where(button => button.DataContext is DesktopFileEntryViewModel).ToArray();
            Check(buttons.Length == 2, presentation.Descriptor.Id + ": both file icons are laid out.");
            var points = buttons.Select(button => button.TranslatePoint(default, host)!.Value).ToArray();
            var start = new Point(points.Min(point => point.X) - 5, points.Min(point => point.Y) - 5);
            var end = new Point(points.Max(point => point.X) + 103, points.Max(point => point.Y) + 105);
            host.MouseDown(start, MouseButton.Left);
            host.MouseMove(end);
            host.MouseUp(end, MouseButton.Left);
            Check(icon.IsDesktopSelected && second.IsDesktopSelected, presentation.Descriptor.Id + ": dragging empty space selects multiple icons.");
            shell.ClearDesktopSelectionCommand.Execute(null);
            host.MouseDown(end, MouseButton.Left);
            host.MouseMove(start);
            host.MouseUp(start, MouseButton.Left);
            Check(icon.IsDesktopSelected && second.IsDesktopSelected, presentation.Descriptor.Id + ": reverse marquee uses normalized desktop coordinates.");
            var center = buttons[0].TranslatePoint(new Point(45, 40), host)!.Value;
            host.MouseDown(center, MouseButton.Left, RawInputModifiers.Control);
            host.MouseUp(center, MouseButton.Left, RawInputModifiers.Control);
            Check(!icon.IsDesktopSelected && second.IsDesktopSelected, presentation.Descriptor.Id + ": Ctrl-click toggles only the pointed icon.");
            host.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            host.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            Check(icon.IsDesktopSelected && second.IsDesktopSelected, presentation.Descriptor.Id + ": Ctrl+A selects every desktop entry.");
            host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            host.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Check(!icon.IsDesktopSelected && !second.IsDesktopSelected, presentation.Descriptor.Id + ": Escape clears desktop selection.");
            host.Close();
            await presentation.DisposeAsync();
            shell.ClearDesktopSelectionCommand.Execute(null);
        }
        await Windows11DesktopChecks.RunAsync();
        var hostSource = new LocalUploadSource("host-selection.bin");
        shell.ReadHostFileClipboardAsync = () => Task.FromResult(new HostFileClipboardSnapshot(false, [hostSource]));
        string? target = null;
        IReadOnlyList<LocalUploadSource>? pasted = null;
        shell.PasteHostFilesAsync = (directory, sources) => { target = directory; pasted = sources; return Task.CompletedTask; };
        await shell.PasteDesktopCommand.ExecuteAsync(null);
        Check(target == "/desktop" && pasted?.Single() == hostSource && remoteCalls == 0,
            "A Windows file selection pastes to the desktop through the shared transfer pipeline.");
        var folder = new DesktopFileEntryViewModel(entry with { Path = "/desktop/folder", Name = "folder", Type = FileSystemEntryType.Directory });
        await shell.PasteDesktopCommand.ExecuteAsync(folder);
        Check(target == "/desktop/folder", "Desktop folder paste captures the selected folder as its destination.");
        shell.ReadHostFileClipboardAsync = () => Task.FromResult(new HostFileClipboardSnapshot(false, []));
        await shell.PasteDesktopCommand.ExecuteAsync(null);
        Check(remoteCalls == 0, "A newer Windows text clipboard cannot revive a stale desktop file selection.");
        clipboard.Set([entry], RemoteFileClipboardOperation.Copy);
        shell.ReadHostFileClipboardAsync = () => Task.FromResult(new HostFileClipboardSnapshot(true, []));
        await shell.PasteDesktopCommand.ExecuteAsync(null);
        Check(remoteCalls == 1, "The active remote file clipboard still routes desktop paste to server-side copy.");
        var button = (Button)typeof(LauncherDesktopShellBase).GetMethod("Icon", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [shell, icon])!;
        Check(button.KeyBindings.Any(binding => binding.Gesture?.Key == Key.C && ReferenceEquals(binding.Command, shell.CopyDesktopEntryCommand))
            && button.KeyBindings.Any(binding => binding.Gesture?.Key == Key.X && ReferenceEquals(binding.Command, shell.CutDesktopEntryCommand))
            && button.KeyBindings.Any(binding => binding.Gesture?.Key == Key.V && ReferenceEquals(binding.Command, shell.PasteDesktopCommand)),
            "Desktop file icons expose copy, cut and paste keyboard shortcuts.");
    }
}
public class DesktopClipboardProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = (_, _) => null;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}

internal sealed class SelectionSurfaces : IShellSurfaceRegistry
{
    public void Clear() { }
    public void Register(ShellSurfaces surfaces) { }
    public void UpdateWorkArea(RelaxKonOS.Core.Primitives.Rect area) { }
}
