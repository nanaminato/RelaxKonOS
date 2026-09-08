using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Shell;

namespace RelaxKonOS.Client.Services;

/// <summary>Catalogues manifests without executing packages; assemblies load only for an explicit activation.</summary>
public sealed class ShellCatalog : IShellCatalog
{
    private readonly Dictionary<string, Func<IDesktopShell>> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExternalPackage> _external = new(StringComparer.Ordinal);
    private readonly DeveloperPackageManager _packages;
    private readonly LocalizationService _localization;
    public event EventHandler? Changed;

    public ShellCatalog(DeveloperPackageManager packages, LocalizationService localization)
    {
        _packages = packages;
        _localization = localization;
        Register(BuiltInShells.Windows, () => new WindowsLikeDesktopShell());
        Register(BuiltInShells.Macos, () => new MacosLikeDesktopShell());
        Register(BuiltInShells.Ubuntu, () => new UbuntuLikeDesktopShell());
        Discover();
        _packages.PackagesChanged += (_, _) => Discover();
        _localization.LanguageChanged += (_, _) => Discover();
    }

    public IReadOnlyList<ShellDescriptor> Available => _factories.Keys
        .Select(id => TryGet(id, out var descriptor) ? descriptor : null).Where(x => x is not null).Cast<ShellDescriptor>()
        .Concat(_external.Values.Select(p => p.Descriptor)).OrderBy(x => x.DisplayName).ToArray();

    public bool TryGet(string id, out ShellDescriptor descriptor)
    {
        id = ShellApi.ResolveId(id);
        if (_factories.TryGetValue(id, out var factory)) { descriptor = factory().Descriptor; return true; }
        if (_external.TryGetValue(id, out var package)) { descriptor = package.Descriptor; return true; }
        descriptor = null!; return false;
    }

    public bool TryCreate(string id, out IDesktopShell? shell, out string? error)
    {
        shell = null; error = null; id = ShellApi.ResolveId(id);
        try
        {
            if (_factories.TryGetValue(id, out var builtIn)) { shell = builtIn(); return true; }
            if (!_external.TryGetValue(id, out var package)) { error = LocalizedText.Get("settings.shell.not_installed", "This desktop package is not installed on this device."); return false; }
            if (!package.Descriptor.IsAvailable) { error = package.Descriptor.UnavailableReason; return false; }
            shell = package.Create(); return true;
        }
        catch (Exception ex) { error = string.Format(LocalizedText.Get("settings.shell.activation_failed", "Desktop package activation failed: {0}"), ex.GetType().Name); return false; }
    }

    public void Disable(string id)
    {
        if (_external.Remove(id)) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Discover()
    {
        _external.Clear();
        foreach (var installed in _packages.Installed)
        {
            if (!IsDesktopShellPackage(installed.InstallationPath)) continue;
            var package = Validate(installed.InstallationPath);
            _external[package.Descriptor.Id] = package;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Register(ShellDescriptor descriptor, Func<IDesktopShell> factory) => _factories.Add(descriptor.Id, factory);

    private ExternalPackage Validate(string root)
    {
        try
        {
            var manifestPath = Path.Combine(root, "manifest.json");
            var manifest = JsonSerializer.Deserialize<ShellManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Manifest is empty.");
            var id = manifest.Id?.Trim() ?? string.Empty;
            var validId = System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{2,127}$") && !id.StartsWith("relaxkonos.", StringComparison.Ordinal);
            var assemblyPath = Path.GetFullPath(Path.Combine(root, manifest.EntryAssembly ?? string.Empty));
            if (!assemblyPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(assemblyPath)) throw new InvalidDataException("Entry assembly is outside the package or missing.");
            var reason = !string.Equals(manifest.PackageType, "desktopShell", StringComparison.Ordinal) ? LocalizedText.Get("settings.shell.invalid_package", "The desktop package is invalid.") :
                !validId ? LocalizedText.Get("settings.shell.invalid_id", "The desktop package ID is invalid.") : manifest.SchemaVersion != 1 ? LocalizedText.Get("settings.shell.unsupported_schema", "This desktop package uses an unsupported manifest schema.") :
                !string.Equals(manifest.ShellApiVersion, ShellApi.Version, StringComparison.Ordinal) ? LocalizedText.Get("settings.shell.requires_newer_api", $"This desktop package requires Shell API {ShellApi.Version}.") :
                manifest.Capabilities?.Length == 0 ? LocalizedText.Get("settings.shell.no_capabilities", "This desktop package declares no launcher capabilities.") : null;
            var displayName = ResolveLocalizedDisplayName(manifest) ?? manifest.DisplayName?.Trim() ?? id;
            var descriptor = new ShellDescriptor(id, displayName, manifest.Version?.Trim() ?? "0.0.0",
                ShellSourceKind.ExternalPackage, ParseCapabilities(manifest.Capabilities), manifest.PackageId ?? id, reason);
            return new ExternalPackage(descriptor, root, assemblyPath, manifest.EntryType ?? string.Empty);
        }
        catch (Exception)
        {
            var id = "invalid-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))).ToLowerInvariant()[..10];
            return new ExternalPackage(new ShellDescriptor(id, Path.GetFileName(root), "0", ShellSourceKind.ExternalPackage,
                ShellCapabilities.None, null, LocalizedText.Get("settings.shell.invalid_package", "The desktop package is invalid.")), root, string.Empty, string.Empty);
        }
    }

    private static ShellCapabilities ParseCapabilities(IEnumerable<string>? values)
    {
        var result = ShellCapabilities.None;
        foreach (var value in values ?? []) result |= value switch { "desktop" => ShellCapabilities.Desktop, "appLauncher" => ShellCapabilities.AppLauncher, "runningApps" => ShellCapabilities.RunningApps, "shellOverlays" => ShellCapabilities.ShellOverlays, _ => ShellCapabilities.None };
        return result;
    }

    private string? ResolveLocalizedDisplayName(ShellManifest manifest)
    {
        if (manifest.LocalizedMetadata is null) return null;
        if (manifest.LocalizedMetadata.TryGetValue(_localization.CurrentLanguage, out var exact)) return exact.DisplayName?.Trim();
        var neutral = _localization.CurrentLanguage.Split('-', 2)[0];
        return manifest.LocalizedMetadata.FirstOrDefault(pair =>
            pair.Key.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase)).Value?.DisplayName?.Trim();
    }
    private static bool IsDesktopShellPackage(string root)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            return document.RootElement.TryGetProperty("packageType", out var packageType)
                && packageType.ValueKind == JsonValueKind.String
                && string.Equals(packageType.GetString(), "desktopShell", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private sealed record ShellManifest(int SchemaVersion, string? PackageType, string? Id, string? DisplayName, string? Version, string? PackageId,
        string? EntryAssembly, string? EntryType, string? ShellApiVersion, string[]? Capabilities, string? Sha256,
        IReadOnlyDictionary<string, ShellLocalizedMetadata>? LocalizedMetadata);
    private sealed record ShellLocalizedMetadata(string? DisplayName, string? Description);
    private sealed class ExternalPackage(ShellDescriptor descriptor, string root, string assemblyPath, string entryType)
    {
        public ShellDescriptor Descriptor { get; } = descriptor; public string Root { get; } = root;
        public IDesktopShell Create()
        {
            var context = new ShellAssemblyLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var factory = (IDesktopShellFactory?)Activator.CreateInstance(assembly.GetType(entryType, throwOnError: true)!);
            var shell = factory?.Create() ?? throw new InvalidDataException("Entry type is not an IDesktopShellFactory.");
            return new LoadedExternalShell(shell, context);
        }
    }
    private sealed class ShellAssemblyLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Contract and UI framework identities must be shared with the host. Package-private
            // dependencies continue to resolve beside the package entry assembly.
            if (assemblyName.Name?.StartsWith("RelaxKonOS.", StringComparison.Ordinal) == true
                || assemblyName.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
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
