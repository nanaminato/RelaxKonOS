using RelaxKonOS.Shell;

namespace RelaxKonOS.Example.Windows11DesktopShell;

public sealed class Windows11ShellFactory : IDesktopShellFactory
{
    public ShellDescriptor Descriptor { get; } = new(
        "com.example.windows11-desktop", "Windows 11 Desktop (External)", "1.3.1",
        ShellSourceKind.ExternalPackage, ShellCapabilities.Desktop | ShellCapabilities.AppLauncher | ShellCapabilities.ShellOverlays,
        "com.example.windows11-desktop");

    public IDesktopShell Create() => new Windows11Shell(Descriptor);
}
