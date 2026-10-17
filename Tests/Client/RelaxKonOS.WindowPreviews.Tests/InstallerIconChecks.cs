using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.AppInstaller.ViewModels;
using RelaxKonOS.Client.Apps.AppInstaller.Views;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.Developer;

internal static class InstallerIconChecks
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: Installer: " + message); }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props"))) directory = directory.Parent;
        var icon = File.ReadAllBytes(Path.Combine(directory!.FullName, "examples", "Windows11DesktopShell", "assets", "icon.png"));
        var manifest = new DeveloperPackageManifest("test.preview", "Icon preview", "1.0.0", "lib/sample.dll", "Sample.App", IconPath: "assets/icon.png");
        using var vm = new AppInstallerViewModel(null!);
        vm.CurrentPackage = new AppPackageCandidate("sample.roapp", "Local sample", manifest, null, false, icon);
        var view = new AppInstallerView { DataContext = vm };
        var host = new Window { Width = 800, Height = 600, Content = view };
        host.Show(); host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Check(vm.HasPackageIcon && view.GetVisualDescendants().OfType<Image>().Any(image => image.IsEffectivelyVisible && ReferenceEquals(image.Source, vm.PackageIcon)), "package artwork renders even when IconGlyph is absent.");
        vm.CurrentPackage = vm.CurrentPackage with { IconData = [1, 2, 3] };
        Check(!vm.HasPackageIcon && !string.IsNullOrWhiteSpace(vm.PackageIconGlyph), "corrupt artwork retains a visible package fallback.");
        vm.CurrentPackage = vm.CurrentPackage with { IconData = null, Manifest = manifest with { IconGlyph = "T" } };
        Check(!vm.HasPackageIcon && vm.PackageIconGlyph == "T", "glyph-only packages keep their declared icon.");
        vm.CurrentPackage = null;
        Check(vm.PackageIcon is null, "clearing the preview releases its image.");
        host.Close();
    }
}
