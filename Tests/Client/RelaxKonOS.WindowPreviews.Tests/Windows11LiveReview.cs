using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.WindowManager;

internal static class Windows11LiveReview
{
    public static async Task RunAsync(IServiceProvider services, Window host, string path, CancellationToken ct)
    {
        var installer = services.GetRequiredService<AppPackageInstallerService>();
        var package = await installer.InspectLocalAsync(path, cancellationToken: ct);
        using (var preview = new AppInstallerViewModel(installer) { CurrentPackage = package })
            if (!preview.HasPackageIcon) throw new Exception("Actual package icon did not decode in installer preview.");
        Console.WriteLine("LIVE WINDOWS11 PASS: actual package icon decodes before installation.");
        await installer.InstallAsync(package, ct);
        var runtime = services.GetRequiredService<ShellRuntime>();
        if (!await runtime.SwitchAsync(package.Manifest.Id, persist: false, ct)) throw new Exception("Cannot activate external Windows 11 shell.");
        await Task.Delay(300, ct);
        host.UpdateLayout();
        var model = host.GetVisualDescendants().OfType<Control>().Select(control => control.DataContext)
            .First(value => value?.GetType().Name == "Windows11ShellViewModel")!;
        ICommand Command(string name) => (ICommand)model.GetType().GetProperty(name)!.GetValue(model)!;
        bool IsOpen() => (bool)model.GetType().GetProperty("IsStartOpen")!.GetValue(model)!;
        Command("ToggleStartCommand").Execute(null);
        Command("OpenSettingsCommand").Execute(null);
        if (IsOpen()) throw new Exception("Settings left Start visible.");
        await Task.Delay(200, ct);
        var manager = services.GetRequiredService<IWindowManager>();
        Command("ToggleStartCommand").Execute(null);
        Command("OpenDisplaySettingsCommand").Execute(null);
        await Task.Delay(200, ct);
        host.UpdateLayout();
        if (IsOpen() || !manager.IsSystemModalOpen) throw new Exception("Display settings did not replace Start with a modal dialog.");
        var dialog = manager.ActiveWindow!;
        var field = dialog.View.GetVisualDescendants().OfType<CheckBox>().First(control => control.IsEffectivelyVisible);
        var point = field.TranslatePoint(new Point(8, 8), host)!.Value;
        var hit = host.InputHitTest(point) as Visual;
        if (hit is null || !(ReferenceEquals(hit, field) || hit.GetVisualAncestors().Contains(field)))
            throw new Exception("External shell dialog is covered by another input surface.");
        Console.WriteLine("LIVE WINDOWS11 PASS: Start dismisses before Settings and display dialog; actual checkbox receives hit testing.");
        var desktop = (RelaxKonOS.Client.ViewModels.Shell.DesktopShellViewModel)host.DataContext!;
        var originalPins = desktop.Settings.PinnedAppIds.ToList();
        var terminalId = new RelaxKonOS.Core.Applications.AppId("relaxkonos.terminal");
        try
        {
            // Inspect the live taskbar without saving a QA pin to the user's remote preferences.
            desktop.Settings.PinnedAppIds = originalPins.Append(terminalId.Value).Distinct().ToList();
            await Task.Delay(200, ct);
            host.UpdateLayout();
            var pinnedButton = host.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.DataContext is RelaxKonOS.Shell.ShellTaskbarEntry entry && entry.Id == terminalId && entry.IsPinned);
            if (pinnedButton is null || !pinnedButton.IsEffectivelyVisible)
                throw new Exception("Pinned closed application did not appear in the actual external taskbar.");
            Console.WriteLine("LIVE WINDOWS11 PASS: a pinned closed application renders in the taskbar beside running applications.");
        }
        finally { desktop.Settings.PinnedAppIds = originalPins; }
        await Task.Delay(200, ct);
        host.UpdateLayout();
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "live-desktops", "windows11-display-dialog.png");
        using (var image = new RenderTargetBitmap(new PixelSize((int)host.Bounds.Width, (int)host.Bounds.Height)))
        { image.Render(host); image.Save(output, new PngBitmapEncoderOptions()); }
        // Cancel through the real view's event handler; no desktop settings are saved.
        var cancel = dialog.View.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.Content?.ToString() == RelaxKonOS.Client.Localization.LocalizedText.Get("common.cancel"));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var count = 0; count < 20 && manager.IsSystemModalOpen; count++) await Task.Delay(50, ct);
        if (manager.IsSystemModalOpen) throw new Exception("Display settings cancellation left the desktop blocked.");
        Console.WriteLine("LIVE WINDOWS11 PASS: dialog cancellation removes its blocker and restores application access.");
        var icons = host.GetVisualDescendants().OfType<Button>().Where(button => button.DataContext is RelaxKonOS.Shell.ShellDesktopEntry).ToArray();
        Console.WriteLine($"LIVE ICON DIAGNOSTIC: rendered={icons.Length}; hostItems={desktop.DesktopItems.Count}; applicationShortcuts={desktop.DesktopIcons.Count}; showApps={desktop.Settings.ShowBuiltInApps}; filterCount={desktop.Settings.VisibleAppIds.Count}; iconsVisible={desktop.AreDesktopIconsVisible}; projectionCount={((System.Collections.ICollection)model.GetType().GetProperty("DesktopEntries")!.GetValue(model)!).Count}");
        if (icons.Length < 12) throw new Exception("Live multi-selection review requires twelve rendered desktop icons.");
        var geometry = icons.Select(button => (button, button.Bounds, point: button.TranslatePoint(default, host))).ToArray();
        desktop.SetDesktopSelection(desktop.DesktopItems.Take(12));
        await Task.Delay(100, ct); host.UpdateLayout();
        if (geometry.Any(entry => entry.button.Bounds != entry.Bounds || entry.button.TranslatePoint(default, host) != entry.point))
            throw new Exception("Actual external desktop icon positions moved when selecting applications.");
        desktop.ClearDesktopSelectionCommand.Execute(null);
        Console.WriteLine("LIVE WINDOWS11 PASS: selecting twelve desktop applications preserves every icon position.");
    }
}
