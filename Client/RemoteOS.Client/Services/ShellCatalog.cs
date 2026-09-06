using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Client.Services.Developer;
using Client.Views.Shell;
using RemoteOS.Shell;

namespace Client.Services;

/// <summary>Catalogues manifests without executing packages; assemblies load only for an explicit activation.</summary>
public sealed class ShellCatalog : IShellCatalog
{
    private readonly Dictionary<string, Func<IDesktopShell>> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExternalPackage> _external = new(StringComparer.Ordinal);
    private readonly DeveloperModeService _developerMode;
    private readonly string _root;
    public event EventHandler? Changed;

    public ShellCatalog(DeveloperModeService developerMode)
    {
        _developerMode = developerMode;
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteOS", "ShellExtensions");
        Register(BuiltInShells.Windows, () => new WindowsLikeDesktopShell());
        Register(BuiltInShells.Macos, () => new MacosLikeDesktopShell());
        Register(BuiltInShells.Ubuntu, () => new UbuntuLikeDesktopShell());
        Discover();
        _developerMode.Changed += (_, _) => Discover();
    }

    public IReadOnlyList<ShellDescriptor> Available => _factories.Keys
        .Select(id => TryGet(id, out var descriptor) ? descriptor : null).Where(x => x is not null).Cast<ShellDescriptor>()
        .Concat(_external.Values.Select(p => p.Descriptor)).OrderBy(x => x.DisplayName).ToArray();

    public bool TryGet(string id, out ShellDescriptor descriptor)
    {
        id = ShellApi.NormalizeId(id);
        if (_factories.TryGetValue(id, out var factory)) { descriptor = factory().Descriptor; return true; }
        if (_external.TryGetValue(id, out var package)) { descriptor = package.Descriptor; return true; }
        descriptor = null!; return false;
    }

    public bool TryCreate(string id, out IDesktopShell? shell, out string? error)
    {
        shell = null; error = null; id = ShellApi.NormalizeId(id);
        try
        {
            if (_factories.TryGetValue(id, out var builtIn)) { shell = builtIn(); return true; }
            if (!_external.TryGetValue(id, out var package)) { error = "Shell is not installed on this device."; return false; }
            if (!package.Descriptor.IsAvailable) { error = package.Descriptor.UnavailableReason; return false; }
            shell = package.Create(); return true;
        }
        catch (Exception ex) { error = $"Package activation failed: {ex.GetType().Name}"; return false; }
    }

    /// <summary>Called only after a user selected a local package folder in Settings.</summary>
    public async Task<ShellDescriptor> InstallAsync(string packageDirectory, CancellationToken cancellationToken = default)
    {
        var package = Validate(packageDirectory);
        if (!package.Descriptor.IsAvailable) throw new InvalidOperationException(package.Descriptor.UnavailableReason);
        var destination = Path.Combine(_root, package.Descriptor.PackageId ?? package.Descriptor.Id);
        Directory.CreateDirectory(_root);
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        await Task.Run(() => CopyDirectory(packageDirectory, destination), cancellationToken);
        Discover();
        return _external.TryGetValue(package.Descriptor.Id, out var installed) ? installed.Descriptor : package.Descriptor;
    }

    public void Disable(string id)
    {
        if (_external.Remove(id)) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Discover()
    {
        _external.Clear();
        if (Directory.Exists(_root))
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                var package = Validate(directory);
                _external[package.Descriptor.Id] = package;
            }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Register(ShellDescriptor descriptor, Func<IDesktopShell> factory) => _factories.Add(descriptor.Id, factory);

    private ExternalPackage Validate(string root)
    {
        try
        {
            var manifestPath = Path.Combine(root, "shell.json");
            var manifest = JsonSerializer.Deserialize<ShellManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Manifest is empty.");
            var id = manifest.Id?.Trim() ?? string.Empty;
            var validId = System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{2,127}$") && !id.StartsWith("remoteos.", StringComparison.Ordinal);
            var assemblyPath = Path.GetFullPath(Path.Combine(root, manifest.EntryAssembly ?? string.Empty));
            if (!assemblyPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(assemblyPath)) throw new InvalidDataException("Entry assembly is outside the package or missing.");
            var reason = !validId ? "Invalid external shell id." : manifest.SchemaVersion != 1 ? "Unsupported manifest schema." :
                manifest.MinimumShellApiVersion > ShellApi.Version ? "This package requires a newer Shell API." :
                manifest.Capabilities?.Length == 0 ? "Package declares no launcher capabilities." : null;
            if (reason is null && !_developerMode.IsEnabled)
            {
                if (string.IsNullOrWhiteSpace(manifest.Sha256)) reason = "Unsigned package. Enable Developer Mode only for trusted development packages.";
                else if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))), manifest.Sha256, StringComparison.OrdinalIgnoreCase)) reason = "Entry assembly hash verification failed.";
            }
            var descriptor = new ShellDescriptor(id, manifest.DisplayName?.Trim() ?? id, manifest.Version?.Trim() ?? "0.0.0",
                ShellSourceKind.ExternalPackage, ParseCapabilities(manifest.Capabilities), manifest.PackageId ?? id, reason);
            return new ExternalPackage(descriptor, root, assemblyPath, manifest.EntryType ?? string.Empty);
        }
        catch (Exception ex)
        {
            var id = "invalid-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))).ToLowerInvariant()[..10];
            return new ExternalPackage(new ShellDescriptor(id, Path.GetFileName(root), "0", ShellSourceKind.ExternalPackage,
                ShellCapabilities.None, null, $"Invalid package: {ex.Message}"), root, string.Empty, string.Empty);
        }
    }

    private static ShellCapabilities ParseCapabilities(IEnumerable<string>? values)
    {
        var result = ShellCapabilities.None;
        foreach (var value in values ?? []) result |= value switch { "desktop" => ShellCapabilities.Desktop, "appLauncher" => ShellCapabilities.AppLauncher, "runningApps" => ShellCapabilities.RunningApps, "shellOverlays" => ShellCapabilities.ShellOverlays, _ => ShellCapabilities.None };
        return result;
    }
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private sealed record ShellManifest(int SchemaVersion, string? Id, string? DisplayName, string? Version, string? PackageId,
        string? EntryAssembly, string? EntryType, int MinimumShellApiVersion, string[]? Capabilities, string? Sha256);
    private sealed class ExternalPackage(ShellDescriptor descriptor, string root, string assemblyPath, string entryType)
    {
        public ShellDescriptor Descriptor { get; } = descriptor; public string Root { get; } = root;
        public IDesktopShell Create()
        {
            var context = new AssemblyLoadContext("RemoteOS.Shell." + Descriptor.Id, isCollectible: true);
            context.Resolving += (_, name) => File.Exists(Path.Combine(Root, "lib", "net10.0", name.Name + ".dll"))
                ? context.LoadFromAssemblyPath(Path.Combine(Root, "lib", "net10.0", name.Name + ".dll")) : null;
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var factory = (IDesktopShellFactory?)Activator.CreateInstance(assembly.GetType(entryType, throwOnError: true)!);
            var shell = factory?.Create() ?? throw new InvalidDataException("Entry type is not an IDesktopShellFactory.");
            return new LoadedExternalShell(shell, context);
        }
    }

    private sealed class LoadedExternalShell(IDesktopShell inner, AssemblyLoadContext context) : IDesktopShell
    {
        public ShellDescriptor Descriptor => inner.Descriptor;
        public Avalonia.Controls.Control View => inner.View;
        public Task InitializeAsync(ShellPresentationContext presentation, CancellationToken cancellationToken) => inner.InitializeAsync(presentation, cancellationToken);
        public Task ActivateAsync(CancellationToken cancellationToken) => inner.ActivateAsync(cancellationToken);
        public Task DeactivateAsync(CancellationToken cancellationToken) => inner.DeactivateAsync(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync(); }
            finally { context.Unload(); }
        }
    }
}
