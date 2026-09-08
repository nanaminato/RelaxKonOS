using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using VirtualSystemDriveService = RelaxKonOS.Client.Services.VirtualSystemDrive.VirtualSystemDrive;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.VirtualSystemDrive;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>
/// Installs development <c>.roapp</c> archives and loads them into their own assembly load context.
/// A development package may never use the reserved <c>remoteos.*</c> identifier range.
/// </summary>
public sealed class DeveloperPackageManager
{
    private readonly ApplicationManager _applications;
    private readonly ExternalAppContextFactory _contextFactory;
    private readonly IAppPermissionManager _permissions;
    private readonly IWindowManager _windowManager;
    private readonly IAppActivationDiagnostics _activationDiagnostics;
    private readonly VirtualSystemDriveService _drive;
    private readonly string _root;
    private readonly string _catalogPath;
    private readonly string _legacyRoot;
    private readonly string _legacyCatalogPath;
    private readonly Dictionary<string, DeveloperAppRecord> _catalog;
    private readonly Dictionary<string, LoadedDeveloperApp> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeveloperAppRecord> _fallbacks = new(StringComparer.Ordinal);

    public DeveloperPackageManager(
        ApplicationManager applications,
        ExternalAppContextFactory contextFactory,
        IWindowManager windowManager,
        IAppPermissionManager permissions,
        IAppActivationDiagnostics activationDiagnostics,
        VirtualSystemDriveService drive)
    {
        _applications = applications;
        _contextFactory = contextFactory;
        _windowManager = windowManager;
        _permissions = permissions;
        _activationDiagnostics = activationDiagnostics;
        _drive = drive;
        _drive.EnsureCreated();
        _root = _drive.ExternalProgramsDirectory;
        _catalogPath = _drive.ResolveRootChild("System/external-catalog.json");
        _legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "developer-apps");
        _legacyCatalogPath = Path.Combine(_legacyRoot, "catalog.json");
        _catalog = LoadCatalog(_catalogPath);
    }

    public IReadOnlyList<DeveloperAppInfo> Installed => _catalog.Values
        .OrderBy(record => record.DisplayName)
        .Select(record => new DeveloperAppInfo(record.Id, record.DisplayName, record.Version, record.Path))
        .ToArray();

    /// <summary>Raised after the unified package catalog changes, including desktop-shell packages.</summary>
    public event EventHandler? PackagesChanged;

    /// <summary>Reads package metadata without extracting or installing the archive.</summary>
    public async Task<DeveloperPackageManifest> InspectAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        await using var package = File.OpenRead(packagePath);
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("The application package must contain manifest.json at its root.");
        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<DeveloperPackageManifest>(manifestStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
            ?? throw new InvalidOperationException("manifest.json is invalid.");
        ValidateManifest(manifest);
        return manifest;
    }

    public DeveloperAppInfo? FindInstalled(string appId) => _catalog.TryGetValue(appId, out var record)
        ? new DeveloperAppInfo(record.Id, record.DisplayName, record.Version, record.Path)
        : null;

    /// <summary>Loads installed packages at client startup. Invalid old packages are ignored instead of breaking the shell.</summary>
    public async Task LoadInstalledAsync(CancellationToken cancellationToken = default)
    {
        // Startup invokes this from Avalonia's UI thread. Package discovery reads JSON from disk,
        // so it must remain asynchronous; synchronously waiting here prevents the UI dispatcher
        // from running the file I/O continuations and stops the login window from being created.
        await DiscoverExternalPackagesAsync(cancellationToken);
        await MigrateLegacyPackagesAsync(cancellationToken);
        CleanupDeferredUninstalls();
        foreach (var record in _catalog.Values.ToArray())
        {
            if (record.PermissionModelVersion != 2)
            {
                _catalog.Remove(record.Id);
                _permissions.Clear(new AppId(record.Id));
                TryDeleteDirectory(AppDirectory(record.Id));
                RecordActivationDiagnostic($"Package rejected: app={record.Id}, reason=permission-model-v2-required");
                continue;
            }
            // Register manifest metadata only. In particular, do not load a package's native
            // dependencies before the compatibility gate has approved its first launch.
            try { Register(record); }
            catch (Exception exception)
            {
                RecordActivationDiagnostic($"Package registration failed: app={record.Id}, error={exception.GetType().Name}: {exception.Message}");
            }
        }
        SaveCatalog(_catalogPath, _catalog);
        PackagesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<DeveloperAppInfo> InstallAsync(Stream package, bool launch, CancellationToken cancellationToken = default)
    {
        var staging = _drive.ResolveUnder(_root, $".staging/{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var manifest = await ExtractAndReadManifestAsync(package, staging, cancellationToken);
            ValidateManifest(manifest);

            var appId = manifest.Id.Trim();
            var version = manifest.Version.Trim();
            // Keep every deployment in a distinct folder. A currently loaded DLL can be locked on
            // Windows, so overwriting a version directory would make the development update flaky.
            var versionId = Guid.NewGuid().ToString("N");
            var destination = VersionPath(appId, versionId);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
            staging = string.Empty;

            var record = new DeveloperAppRecord(appId, manifest.DisplayName.Trim(), version, destination, manifest.EntryAssembly.Trim(), manifest.EntryType.Trim(),
                manifest.IconGlyph, ResolveIconPath(destination, manifest.IconPath), manifest.Description, manifest.RequestedPermissions ?? Array.Empty<string>(),
                manifest.SupportedFileExtensions ?? Array.Empty<string>(), manifest.LocalizedMetadata,
                manifest.ClientPlatforms ?? Array.Empty<string>(), manifest.ServerRequirements,
                manifest.SupportedFileNames ?? Array.Empty<string>(), manifest.SupportsExtensionlessFiles,
                ParseInstancePolicy(manifest.InstancePolicy), manifest.SupportedUriSchemes ?? Array.Empty<string>(), manifest.PermissionModelVersion);
            if (_catalog.TryGetValue(appId, out var previous))
                _fallbacks[appId] = previous;
            // This method is invoked from the installer command on Avalonia's UI thread. Do not
            // synchronously wait for asynchronous file I/O here: its continuation may need that
            // same synchronization context, leaving the installer permanently busy.
            await _drive.WriteJsonAtomicallyAsync(_drive.ResolveUnder(destination, "app.remoteos.json"), ToDescriptor(manifest), cancellationToken);
            await _drive.WriteJsonAtomicallyAsync(CurrentPath(appId), new ExternalCurrentVersion(1, appId, versionId), cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => Register(record));

            // A package update is a new authorization subject even when its AppId is stable.
            _permissions.Clear(new AppId(appId));
            _catalog[appId] = record;
            SaveCatalog(_catalogPath, _catalog);
            PackagesChanged?.Invoke(this, EventArgs.Empty);
            if (launch && !IsDesktopShellPackage(destination))
                await Dispatcher.UIThread.InvokeAsync(() => _applications.Launch(new AppId(appId)));
            return new DeveloperAppInfo(record.Id, record.DisplayName, record.Version, record.Path);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(staging) && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<bool> UninstallAsync(string appId)
    {
        if (!_catalog.Remove(appId))
            return false;

        await Dispatcher.UIThread.InvokeAsync(() => UnregisterAndUnload(appId));
        SaveCatalog(_catalogPath, _catalog);
        // A collectible context has been unloaded, but Windows can retain a DLL lock briefly.
        // The application is already unregistered and absent from the catalog; defer deleting
        // a locked directory until the next startup instead of reporting a false uninstall failure.
        // Keep version files for deferred cleanup. current.json is removed first so discovery and
        // the runtime agree that this app is no longer installed even if a DLL remains locked.
        var current = CurrentPath(appId);
        if (File.Exists(current)) File.Delete(current);
        TryDeleteDirectory(AppDirectory(appId));
        PackagesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public Task<bool> LaunchAsync(string appId) => Dispatcher.UIThread.InvokeAsync(() => _applications.Launch(new AppId(appId))).GetTask();

    private LoadedDeveloperApp CreateLoaded(DeveloperAppRecord record)
    {
        var assemblyPath = ResolvePackagePath(record.Path, record.EntryAssembly);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("The package entry assembly was not found.", assemblyPath);

        var context = new DeveloperAssemblyLoadContext(assemblyPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(record.EntryType, throwOnError: false)
                ?? throw new InvalidOperationException($"Package entry type '{record.EntryType}' was not found.");
            if (Activator.CreateInstance(type) is not IExternalRemoteApplication application)
                throw new InvalidOperationException("The package entry type must implement IExternalRemoteApplication.");

            return new LoadedDeveloperApp(context, application);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private void Register(DeveloperAppRecord record)
    {
        UnregisterAndUnload(record.Id);
        if (IsDesktopShellPackage(record.Path)) return;
        _applications.Register(record.SupportedFileExtensions.Count > 0 || (record.SupportedFileNames?.Count ?? 0) > 0 || record.SupportsExtensionlessFiles
            ? new ExternalFileApplicationAdapter(record, this, _contextFactory)
            : new ExternalApplicationAdapter(record, this, _contextFactory));
    }

    private void UnregisterAndUnload(string appId)
    {
        var id = new AppId(appId);
        foreach (var window in _windowManager.Windows.Where(window => window.Info.OwnerAppId == id).ToArray())
            _windowManager.Close(window);

        _applications.Unregister(id);
        UnloadLoaded(appId);
    }

    private void UnloadLoaded(string appId)
    {
        if (_loaded.Remove(appId, out var loaded))
        {
            loaded.LoadContext.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private LoadedDeveloperApp GetOrLoad(DeveloperAppRecord record)
    {
        if (_loaded.TryGetValue(record.Id, out var existing))
            return existing;

        LoadedDeveloperApp loaded;
        try { loaded = CreateLoaded(record); }
        catch
        {
            RestoreFallback(record);
            throw;
        }
        _loaded[record.Id] = loaded;
        return loaded;
    }

    private void RestoreFallback(DeveloperAppRecord failed)
    {
        if (!_fallbacks.Remove(failed.Id, out var fallback))
            return;
        try
        {
            var versionId = Path.GetFileName(fallback.Path);
            _drive.WriteJsonAtomicallyAsync(CurrentPath(fallback.Id), new ExternalCurrentVersion(1, fallback.Id, versionId)).GetAwaiter().GetResult();
            _catalog[fallback.Id] = fallback;
            Register(fallback);
            SaveCatalog(_catalogPath, _catalog);
            RecordActivationDiagnostic($"VSD package rollback: app={fallback.Id}, result=previous-version-restored.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or VirtualSystemDriveException)
        {
            RecordActivationDiagnostic($"VSD package rollback: app={SafeDiagnosticId(failed.Id)}, code={VirtualSystemDriveProblemCode.PackageLayoutInvalid}.");
        }
    }

    internal void RecordActivationDiagnostic(string message) => _activationDiagnostics.Record(message);

    private static ApplicationManifest ToApplicationManifest(DeveloperAppRecord record) => new(
        new AppId(record.Id), record.DisplayName, record.Version, record.IconGlyph, record.Description,
        record.RequestedPermissions, record.SupportedFileExtensions, record.LocalizedMetadata,
        record.ClientPlatforms, record.ServerRequirements, record.SupportedFileNames, record.SupportsExtensionlessFiles,
        SupportsTextFiles: false,
        InstancePolicy: record.InstancePolicy,
        SupportedUriSchemes: record.SupportedUriSchemes,
        IconPath: record.IconPath,
        PermissionModelVersion: record.PermissionModelVersion);

    private static ApplicationDescriptor ToDescriptor(DeveloperPackageManifest manifest) => new(
        ApplicationDescriptorValidator.CurrentSchemaVersion, manifest.Id.Trim(), ApplicationDescriptorKind.Package,
        manifest.DisplayName.Trim(), manifest.Version.Trim(),
        new ApplicationDescriptorActivation(EntryAssembly: manifest.EntryAssembly.Trim(), EntryType: manifest.EntryType.Trim()),
        manifest.Description, new ApplicationDescriptorIcon(manifest.IconPath, manifest.IconGlyph),
        manifest.RequestedPermissions, manifest.SupportedFileExtensions, manifest.SupportedUriSchemes,
        manifest.InstancePolicy, manifest.ClientPlatforms, manifest.PermissionModelVersion);

    private static ApplicationDescriptor ToDescriptor(DeveloperAppRecord record) => new(
        ApplicationDescriptorValidator.CurrentSchemaVersion, record.Id, ApplicationDescriptorKind.Package,
        record.DisplayName, record.Version, new ApplicationDescriptorActivation(EntryAssembly: record.EntryAssembly, EntryType: record.EntryType),
        record.Description, new ApplicationDescriptorIcon(Glyph: record.IconGlyph), record.RequestedPermissions,
        record.SupportedFileExtensions, record.SupportedUriSchemes, record.InstancePolicy.ToString(),
        record.ClientPlatforms, record.PermissionModelVersion);

    /// <summary>Rebuilds the package cache from fixed VSD current pointers without loading DLLs.</summary>
    private async Task DiscoverExternalPackagesAsync(CancellationToken cancellationToken)
    {
        _catalog.Clear();
        foreach (var rawDirectory in Directory.EnumerateDirectories(_root))
        {
            var appId = Path.GetFileName(rawDirectory);
            if (appId.Equals(".staging", StringComparison.Ordinal) || !ApplicationDescriptorValidator.IsValidAppId(appId))
                continue;
            try
            {
                var appDirectory = AppDirectory(appId);
                var current = await _drive.ReadJsonAsync<ExternalCurrentVersion>(CurrentPath(appId), cancellationToken);
                if (current.SchemaVersion != 1 || current.AppId != appId || !IsVersionId(current.VersionId))
                    throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PackageLayoutInvalid);
                var versionDirectory = _drive.ResolveUnder(appDirectory, $"versions/{current.VersionId}");
                var descriptor = await _drive.ReadJsonAsync<ApplicationDescriptor>(
                    _drive.ResolveUnder(versionDirectory, "app.remoteos.json"), cancellationToken);
                var manifest = await _drive.ReadJsonAsync<DeveloperPackageManifest>(
                    _drive.ResolveUnder(versionDirectory, "manifest.json"), cancellationToken);
                ValidateManifest(manifest);
                var validation = ApplicationDescriptorValidator.Validate(descriptor);
                if (!validation.IsValid || descriptor.Kind != ApplicationDescriptorKind.Package || descriptor.Id != appId
                    || descriptor.Activation.EntryAssembly != manifest.EntryAssembly.Trim() || descriptor.Activation.EntryType != manifest.EntryType.Trim())
                    throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PackageLayoutInvalid);

                _catalog[appId] = new DeveloperAppRecord(appId, manifest.DisplayName.Trim(), manifest.Version.Trim(), versionDirectory,
                    manifest.EntryAssembly.Trim(), manifest.EntryType.Trim(), manifest.IconGlyph, ResolveIconPath(versionDirectory, manifest.IconPath),
                    manifest.Description, manifest.RequestedPermissions ?? Array.Empty<string>(), manifest.SupportedFileExtensions ?? Array.Empty<string>(),
                    manifest.LocalizedMetadata, manifest.ClientPlatforms ?? Array.Empty<string>(), manifest.ServerRequirements,
                    manifest.SupportedFileNames ?? Array.Empty<string>(), manifest.SupportsExtensionlessFiles, ParseInstancePolicy(manifest.InstancePolicy),
                    manifest.SupportedUriSchemes ?? Array.Empty<string>(), manifest.PermissionModelVersion);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or VirtualSystemDriveException)
            {
                var code = exception is VirtualSystemDriveException vsd
                    ? vsd.ProblemCode
                    : VirtualSystemDriveProblemCode.PackageLayoutInvalid;
                // Keep the directory intact: an update can repair a damaged descriptor, and
                // deleting it here makes a transient parser mismatch look like an uninstall.
                RecordActivationDiagnostic($"VSD package discovery: app={SafeDiagnosticId(appId)}, code={code}, error={exception.GetType().Name}.");
            }
        }
    }

    /// <summary>Copies, rather than deletes, legacy development packages into the VSD layout.</summary>
    private async Task MigrateLegacyPackagesAsync(CancellationToken cancellationToken)
    {
        foreach (var legacy in LoadCatalog(_legacyCatalogPath).Values)
        {
            if (_catalog.ContainsKey(legacy.Id)) continue;
            try
            {
                ValidateAppId(legacy.Id);
                if (legacy.PermissionModelVersion != 2 || !Directory.Exists(legacy.Path)) throw new InvalidOperationException();
                var legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_legacyRoot)) + Path.DirectorySeparatorChar;
                var source = Path.GetFullPath(legacy.Path);
                if (!source.StartsWith(legacyRoot, StringComparison.Ordinal)) throw new InvalidOperationException();

                var versionId = $"legacy-{Guid.NewGuid():N}";
                var destination = VersionPath(legacy.Id, versionId);
                CopyDirectoryWithoutLinks(source, destination);
                var migrated = legacy with { Path = destination, IconPath = null };
                await _drive.WriteJsonAtomicallyAsync(_drive.ResolveUnder(destination, "app.remoteos.json"), ToDescriptor(migrated), cancellationToken);
                await _drive.WriteJsonAtomicallyAsync(CurrentPath(migrated.Id), new ExternalCurrentVersion(1, migrated.Id, versionId), cancellationToken);
                _catalog.Add(migrated.Id, migrated);
                RecordActivationDiagnostic($"VSD package migration: app={migrated.Id}, result=migrated.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or VirtualSystemDriveException)
            {
                RecordActivationDiagnostic($"VSD package migration: app={SafeDiagnosticId(legacy.Id)}, result=retained-legacy, code={VirtualSystemDriveProblemCode.PackageLayoutInvalid}.");
            }
        }
        SaveCatalog(_catalogPath, _catalog);
    }

    private static void CopyDirectoryWithoutLinks(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(relative == "." ? destination : Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string SafeDiagnosticId(string value) => ApplicationDescriptorValidator.IsValidAppId(value) ? value : "<unknown>";

    private async Task<DeveloperPackageManifest> ExtractAndReadManifestAsync(Stream package, string destination, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("A development package must contain manifest.json at its root.");
        DeveloperPackageManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<DeveloperPackageManifest>(manifestStream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
                ?? throw new InvalidOperationException("manifest.json is invalid.");
        }

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The package contains an invalid path.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = File.Create(target);
            await source.CopyToAsync(output, cancellationToken);
        }

        return manifest;
    }

    private static void ValidateManifest(DeveloperPackageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, "^[a-z0-9][a-z0-9.-]{2,127}$"))
            throw new InvalidOperationException("Application id must use lowercase letters, digits, dots, or hyphens.");
        if (manifest.Id.StartsWith("remoteos.", StringComparison.Ordinal))
            throw new InvalidOperationException("The remoteos.* application id range is reserved for built-in applications.");
        if (manifest.PermissionModelVersion != 2)
            throw new InvalidOperationException("This package uses an unsupported permission model. Rebuild it with permissionModelVersion: 2.");
        if (manifest.PackageType is not null and not "application" and not "desktopShell")
            throw new InvalidOperationException("packageType must be application or desktopShell.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new InvalidOperationException("manifest.json is missing a required field.");
        var entryAssembly = manifest.EntryAssembly.Replace('\\', '/');
        if (!ApplicationDescriptorValidator.IsSafeRelativePath(entryAssembly)
            || !entryAssembly.StartsWith("lib/", StringComparison.Ordinal)
            || !entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("entryAssembly must point to a DLL under lib/.");
        if (!string.IsNullOrWhiteSpace(manifest.IconPath))
        {
            var iconPath = manifest.IconPath.Replace('\\', '/');
            var extension = Path.GetExtension(iconPath);
            if (!ApplicationDescriptorValidator.IsSafeRelativePath(iconPath)
                || !new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("iconPath must be a package-relative PNG, JPEG, WebP, BMP, GIF, or ICO file.");
        }
        if (manifest.LocalizedMetadata?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value.DisplayName)) == true)
            throw new InvalidOperationException("localizedMetadata must use non-empty culture names and display names.");
        if (manifest.SupportedUriSchemes?.Any(scheme => string.IsNullOrWhiteSpace(scheme)
                || !System.Text.RegularExpressions.Regex.IsMatch(scheme, "^[a-z][a-z0-9+.-]{0,31}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                || scheme.Equals("remoteos", StringComparison.OrdinalIgnoreCase)) == true)
            throw new InvalidOperationException("supportedUriSchemes must contain non-reserved URI schemes.");
        _ = ParseInstancePolicy(manifest.InstancePolicy);
    }

    private static bool IsDesktopShellPackage(string packageRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "manifest.json")));
            return document.RootElement.TryGetProperty("packageType", out var packageType)
                && packageType.ValueKind == JsonValueKind.String
                && string.Equals(packageType.GetString(), "desktopShell", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static ApplicationInstancePolicy ParseInstancePolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ApplicationInstancePolicy.MultiWindow;
        return Enum.TryParse<ApplicationInstancePolicy>(value, ignoreCase: true, out var policy)
            ? policy
            : throw new InvalidOperationException("instancePolicy must be MultiWindow, SingleWindow, or SingleWindowPerActivationKey.");
    }

    private string AppDirectory(string appId)
    {
        ValidateAppId(appId);
        return _drive.ResolveUnder(_root, appId);
    }

    private string VersionPath(string appId, string versionId) => _drive.ResolveUnder(AppDirectory(appId), $"versions/{versionId}");
    private string CurrentPath(string appId) => _drive.ResolveUnder(AppDirectory(appId), "current.json");

    private static bool IsVersionId(string? versionId) => !string.IsNullOrWhiteSpace(versionId)
        && versionId.Length <= 128
        && !versionId.Contains('/')
        && ApplicationDescriptorValidator.IsSafeRelativePath(versionId);

    /// <summary>Removes package directories left behind by a prior successful logical uninstall.</summary>
    private void CleanupDeferredUninstalls()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var appId = Path.GetFileName(directory);
            if (appId.Equals(".staging", StringComparison.OrdinalIgnoreCase) || _catalog.ContainsKey(appId))
                continue;
            // Uninstall removes current.json before attempting to remove its directory. A
            // package that still has a current pointer is installed but failed discovery, so it
            // must remain available for recovery or a later repaired startup.
            if (ApplicationDescriptorValidator.IsValidAppId(appId)
                && File.Exists(Path.Combine(directory, "current.json")))
                continue;
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { /* File is still locked; retry on the next client start. */ }
        catch (UnauthorizedAccessException) { /* File is still locked; retry on the next client start. */ }
    }

    private static string ResolvePackagePath(string packageRoot, string relativePath)
    {
        var root = Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package path is outside its installation directory.");
        return resolved;
    }

    private static string? ResolveIconPath(string packageRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var resolved = ResolvePackagePath(packageRoot, relativePath);
        if (!File.Exists(resolved))
            throw new InvalidOperationException("iconPath does not refer to a file in the application package.");
        return resolved;
    }

    private static void ValidateAppId(string appId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(appId, "^[a-z0-9][a-z0-9.-]{2,127}$") || appId.StartsWith("remoteos.", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid developer application id.");
    }

    private static Dictionary<string, DeveloperAppRecord> LoadCatalog(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, DeveloperAppRecord>>(File.ReadAllText(path))
                    ?? new Dictionary<string, DeveloperAppRecord>(StringComparer.Ordinal);
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new Dictionary<string, DeveloperAppRecord>(StringComparer.Ordinal);
    }

    private static void SaveCatalog(string path, Dictionary<string, DeveloperAppRecord> catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class DeveloperAssemblyLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("RelaxKonOS.", StringComparison.Ordinal) == true
                || assemblyName.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    private sealed record LoadedDeveloperApp(
        DeveloperAssemblyLoadContext LoadContext,
        IExternalRemoteApplication Application);

    private class ExternalApplicationAdapter : RemoteApplicationBase, IAppActivationHandler
    {
        protected readonly DeveloperAppRecord Record;
        protected readonly DeveloperPackageManager Owner;
        protected readonly ExternalAppContextFactory ContextFactory;

        public ExternalApplicationAdapter(DeveloperAppRecord record, DeveloperPackageManager owner, ExternalAppContextFactory contextFactory)
        {
            Record = record;
            Owner = owner;
            ContextFactory = contextFactory;
        }

        public override ApplicationManifest Manifest => ToApplicationManifest(Record);

        public override void Activate(AppContext context) => _ = ActivateAsync(context);

        protected async Task ActivateAsync(AppContext context)
        {
            try
            {
                await Owner.GetOrLoad(Record).Application.ActivateAsync(ContextFactory.Create(Manifest));
            }
            catch (Exception exception)
            {
                Owner.RecordActivationDiagnostic($"External app launch failed: app={Manifest.Id.Value}, error={exception.GetType().Name}: {exception.Message}");
                ShowFailure(context, exception);
            }
        }

        protected void ShowFailure(AppContext context, Exception exception) =>
            context.ShowWindow($"{Manifest.DisplayName} failed to start",
                new TextBlock { Text = exception.Message, Margin = new Avalonia.Thickness(20), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                iconGlyph: Manifest.IconGlyph, canResize: true);

        public bool CanHandleActivation(Uri uri)
        {
            if (!Manifest.UriSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
                return false;
            try
            {
                return Owner.GetOrLoad(Record).Application is IExternalAppActivationHandler handler
                    && handler.CanHandleActivation(uri);
            }
            catch (Exception exception)
            {
                Owner.RecordActivationDiagnostic($"External handler eligibility check failed: app={Manifest.Id.Value}, uri={FormatUri(uri)}, error={exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow) =>
            _ = HandleActivationAsync(context, request);

        private async Task HandleActivationAsync(AppContext context, AppActivationRequest request)
        {
            try
            {
                if (Owner.GetOrLoad(Record).Application is IExternalAppActivationHandler handler)
                    await handler.HandleActivationAsync(ContextFactory.Create(Manifest), request.Uri);
            }
            catch (Exception exception)
            {
                Owner.RecordActivationDiagnostic($"External app activation failed: app={Manifest.Id.Value}, uri={FormatUri(request.Uri)}, error={exception.GetType().Name}: {exception.Message}");
                ShowFailure(context, exception);
            }
        }

        private static string FormatUri(Uri uri) => $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
    }

    private sealed class ExternalFileApplicationAdapter(DeveloperAppRecord record, DeveloperPackageManager owner, ExternalAppContextFactory contextFactory)
        : ExternalApplicationAdapter(record, owner, contextFactory), IFileOpenApplication
    {
        public void OpenFile(AppContext context, string path) => _ = OpenFileAsync(context, path);

        private async Task OpenFileAsync(AppContext context, string path)
        {
            try
            {
                await ((IExternalFileOpenApplication)Owner.GetOrLoad(Record).Application)
                    .OpenFileAsync(ContextFactory.Create(Manifest), path);
            }
            catch (Exception exception)
            {
                ShowFailure(context, exception);
            }
        }
    }
}

/// <summary>Development package manifest stored as <c>manifest.json</c> inside a <c>.roapp</c> archive.</summary>
public sealed record DeveloperPackageManifest(
    string Id,
    string DisplayName,
    string Version,
    string EntryAssembly,
    string EntryType,
    string? IconGlyph = null,
    string? Description = null,
    IReadOnlyList<string>? RequestedPermissions = null,
    IReadOnlyList<string>? SupportedFileExtensions = null,
    IReadOnlyDictionary<string, ApplicationLocalizedMetadata>? LocalizedMetadata = null,
    IReadOnlyList<string>? ClientPlatforms = null,
    ApplicationServerRequirements? ServerRequirements = null,
    IReadOnlyList<string>? SupportedFileNames = null,
    bool SupportsExtensionlessFiles = false,
    string? InstancePolicy = null,
    IReadOnlyList<string>? SupportedUriSchemes = null,
    string? IconPath = null,
    int PermissionModelVersion = 0,
    string? PackageType = null,
    // Desktop-shell packages include these package-level fields. They are not used by normal
    // applications, but must be represented here because VSD reads manifests strictly.
    int SchemaVersion = 1,
    string? ShellApiVersion = null,
    IReadOnlyList<string>? Capabilities = null,
    string? PackageId = null,
    string? Sha256 = null);

internal sealed record DeveloperAppRecord(
    string Id,
    string DisplayName,
    string Version,
    string Path,
    string EntryAssembly,
    string EntryType,
    string? IconGlyph,
    string? IconPath,
    string? Description,
    IReadOnlyList<string> RequestedPermissions,
    IReadOnlyList<string> SupportedFileExtensions,
    IReadOnlyDictionary<string, ApplicationLocalizedMetadata>? LocalizedMetadata = null,
    IReadOnlyList<string>? ClientPlatforms = null,
    ApplicationServerRequirements? ServerRequirements = null,
    IReadOnlyList<string>? SupportedFileNames = null,
    bool SupportsExtensionlessFiles = false,
    ApplicationInstancePolicy InstancePolicy = ApplicationInstancePolicy.MultiWindow,
    IReadOnlyList<string>? SupportedUriSchemes = null,
    int PermissionModelVersion = 0);

internal sealed record ExternalCurrentVersion(int SchemaVersion, string AppId, string VersionId);

public sealed record DeveloperAppInfo(string Id, string DisplayName, string Version, string InstallationPath);
