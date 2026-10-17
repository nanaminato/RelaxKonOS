using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using RelaxKonOS.Client.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Shell;

internal static class DesktopLiveReview
{
    public static async Task RunAsync(IServiceProvider services, Window host, CancellationToken ct)
    {
        var runtime = services.GetRequiredService<ShellRuntime>();
        var vm = (DesktopShellViewModel)host.DataContext!;
        var original = runtime.ActiveShellId;
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "live-desktops");
        Directory.CreateDirectory(output);
        try
        {
            foreach (var id in new[] { BuiltInShells.Windows.Id, BuiltInShells.Macos.Id, BuiltInShells.Ubuntu.Id })
            {
                if (!await runtime.SwitchAsync(id, persist: false, ct)) throw new Exception("Cannot activate " + id);
                await Task.Delay(600, ct);
                host.UpdateLayout();
                vm.SetDesktopSelection(vm.DesktopItems.Take(3));
                var selected = vm.DesktopItems.Count(DesktopShellViewModel.IsDesktopItemSelected);
                if (selected != Math.Min(3, vm.DesktopItems.Count)) throw new Exception("Live desktop selection failed.");
                using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(host.Bounds.Width), (int)Math.Ceiling(host.Bounds.Height)));
                image.Render(host);
                image.Save(Path.Combine(output, id + ".png"), new PngBitmapEncoderOptions());
                Console.WriteLine($"LIVE DESKTOP PASS: {id}; entries={vm.DesktopItems.Count}; selected={selected}; screenshot captured.");
                vm.ClearDesktopSelectionCommand.Execute(null);
            }
        }
        finally { await runtime.SwitchAsync(original, persist: false, ct); }
        Console.WriteLine("LIVE DESKTOP REVIEW PASS: " + output);
    }
}
