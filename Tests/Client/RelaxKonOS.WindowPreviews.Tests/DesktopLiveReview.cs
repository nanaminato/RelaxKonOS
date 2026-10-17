using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using RelaxKonOS.Client.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;

internal static class DesktopLiveReview
{
    public static async Task RunAsync(IServiceProvider services, Window host, CancellationToken ct)
    {
        var runtime = services.GetRequiredService<ShellRuntime>();
        var vm = (DesktopShellViewModel)host.DataContext!;
        var original = runtime.ActiveShellId;
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "live-desktops");
        Directory.CreateDirectory(output);
        var windows = services.GetRequiredService<IWindowManager>();
        var existing = windows.Windows.Select(window => window.Info.Id).ToHashSet();
        var applications = services.GetRequiredService<ApplicationManager>();
        try
        {
            applications.Launch(new AppId("relaxkonos.explorer"));
            applications.Launch(new AppId("relaxkonos.codeeditor"));
            await Task.Delay(600, ct);
            var running = windows.Windows.Select(window => window.Info.Id).ToHashSet();
            if (running.Count < 2 || !vm.TaskbarGroups.Any(group => group.HasIconImage))
                throw new Exception("Live switch review requires running applications with image icons.");
            foreach (var id in new[] { BuiltInShells.Windows.Id, BuiltInShells.Macos.Id, BuiltInShells.Ubuntu.Id })
            {
                if (!await runtime.SwitchAsync(id, persist: false, ct)) throw new Exception("Cannot activate " + id);
                await Task.Delay(600, ct);
                host.UpdateLayout();
                if (!running.SetEquals(windows.Windows.Select(window => window.Info.Id)))
                    throw new Exception("Live shell switch lost or replaced running application windows.");
                vm.SetDesktopSelection(vm.DesktopItems.Take(3));
                var selected = vm.DesktopItems.Count(DesktopShellViewModel.IsDesktopItemSelected);
                if (selected != Math.Min(3, vm.DesktopItems.Count)) throw new Exception("Live desktop selection failed.");
                using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(host.Bounds.Width), (int)Math.Ceiling(host.Bounds.Height)));
                image.Render(host);
                image.Save(Path.Combine(output, id + ".png"), new PngBitmapEncoderOptions());
                Console.WriteLine($"LIVE DESKTOP PASS: {id}; entries={vm.DesktopItems.Count}; selected={selected}; windows={running.Count}; screenshot captured.");
                vm.ClearDesktopSelectionCommand.Execute(null);
            }
            if (Environment.GetEnvironmentVariable("RK_REVIEW_EXTERNAL_PACKAGE") is { Length: > 0 } packagePath)
                await Windows11LiveReview.RunAsync(services, host, packagePath, ct);
        }
        finally
        {
            foreach (var window in windows.Windows.Where(window => !existing.Contains(window.Info.Id)).ToArray())
                windows.ForceClose(window);
            await runtime.SwitchAsync(original, persist: false, ct);
        }
        Console.WriteLine("LIVE DESKTOP REVIEW PASS: " + output);
    }
}
