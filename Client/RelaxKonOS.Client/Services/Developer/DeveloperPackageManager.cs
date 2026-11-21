using System.Collections.Concurrent;
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
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>
/// Installs development <c>.roapp</c> archives and loads them into their own assembly load context.
/// Reserved application IDs require a host-approved installation origin.
/// </summary>
public sealed partial class DeveloperPackageManager
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
    private readonly ConcurrentDictionary<string, DeveloperAppRecord> _catalog;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly OfficialDesktopPackages _official;

    public DeveloperPackageManager(
        ApplicationManager applications,
        ExternalAppContextFactory contextFactory,
        IWindowManager windowManager,
        IAppPermissionManager permissions,
        IAppActivationDiagnostics activationDiagnostics,
        VirtualSystemDriveService drive)
        : this(applications, contextFactory, windowManager, permissions, activationDiagnostics, drive,
            Path.Combine(System.AppContext.BaseDirectory, "OfficialApps"))
    {
    }

    internal DeveloperPackageManager(
        ApplicationManager applications,
        ExternalAppContextFactory contextFactory,
        IWindowManager windowManager,
        IAppPermissionManager permissions,
        IAppActivationDiagnostics activationDiagnostics,
        VirtualSystemDriveService drive,
        string officialDirectory, HttpClient? officialHttpClient = null)
    {
        _applications = applications;
        _contextFactory = contextFactory;
        _windowManager = windowManager;
        _permissions = permissions;
        _activationDiagnostics = activationDiagnostics;
        _drive = drive;
        _official = new OfficialDesktopPackages(Path.GetFullPath(officialDirectory), officialHttpClient);
        _drive.EnsureCreated();
        _root = _drive.ExternalProgramsDirectory;
        _catalogPath = _drive.ResolveRootChild("System/external-catalog.json");
        _legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "developer-apps");
        _legacyCatalogPath = Path.Combine(_legacyRoot, "catalog.json");
        _catalog = new(LoadCatalog(_catalogPath), StringComparer.Ordinal);
    }

    internal OfficialDesktopPackages OfficialSource => _official;
    internal IReadOnlyList<DeveloperAppRecord> InstalledRecords => _catalog.Values.ToArray();
    internal IReadOnlyList<DeploymentRequirement> RequiredDependencies(AppId id) =>
        _catalog.GetValueOrDefault(id.Value)?.Dependencies?.Required ?? [];

    internal IReadOnlyList<string> RequiredBy(DeploymentPackageKey package) =>
        _catalog.Values.Where(record => (package.Kind != DeploymentPackageKind.Application || record.Id != package.Id)
                && record.Dependencies?.Required?.Any(requirement => requirement.Package == package) == true)
            .Select(record => record.Id).Order(StringComparer.Ordinal).ToArray();

    internal void EnsureReplacementVersionIsCompatible(string appId, string version)
    {
        var key = new DeploymentPackageKey(DeploymentPackageKind.Application, appId);
        var blocked = _catalog.Values.Where(record => record.Id != appId
                && record.Dependencies?.Required?.Any(requirement => requirement.Package == key
                    && !DeploymentResolver.IsVersionAllowed(version, requirement)) == true)
            .Select(record => record.Id).Order(StringComparer.Ordinal).ToArray();
        if (blocked.Length > 0)
            throw new InvalidOperationException($"Version {version} does not satisfy {string.Join(", ", blocked)}.");
    }

    internal async Task<DeveloperAppInfo?> FindDeploymentResultAsync(string appId, Guid planId, CancellationToken cancellationToken)
    {
        if (FindInstalled(appId) is not { } installed) return null;
        var receipt = _drive.ResolveUnder(installed.InstallationPath, "deployment-receipt.json");
        return File.Exists(receipt) && await _drive.ReadJsonAsync<Guid>(receipt, cancellationToken) == planId
            ? installed : null;
    }

    public IReadOnlyList<DeveloperAppInfo> Installed => _catalog.Values
        .OrderBy(record => record.DisplayName)
        .Select(record => new DeveloperAppInfo(record.Id, record.DisplayName, record.Version, record.Path))
        .ToArray();

    /// <summary>Raised after the unified package catalog changes, including desktop-shell packages.</summary>
    public event EventHandler? PackagesChanged;

    /// <summary>Reads package metadata without extracting or installing the archive.</summary>
    internal async Task<(DeveloperPackageManifest Manifest, string? OfficialSha256)> InspectAsync(
        string packagePath, CancellationToken cancellationToken = default)
    {
        await using var package = File.OpenRead(packagePath);
        DeveloperPackageManifest manifest;
        using (var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
            manifest = await ReadArchiveManifestAsync(archive, cancellationToken);
        if (!OfficialDesktopPackages.IsReserved(manifest.Id))
        {
            ValidateManifest(manifest, PackageOrigin.LocalPackage);
            if (manifest.Dependencies is null) return (manifest, null);
            package.Position = 0;
            return (manifest, Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(package, cancellationToken)));
        }

        ValidateManifest(manifest, PackageOrigin.OfficialDistribution);
        return (manifest, await _official.VerifyAsync(packagePath, manifest.Id, package, cancellationToken));
    }

    private static async Task<DeveloperPackageManifest> ReadArchiveManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("The application package must contain manifest.json at its root.");
        if (manifestEntry.Length > VirtualSystemDriveService.MaximumJsonBytes)
            throw new InvalidOperationException("manifest.json exceeds the maximum size.");
        await using var manifestStream = manifestEntry.Open();
        return await JsonSerializer.DeserializeAsync<DeveloperPackageManifest>(manifestStream,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            }, cancellationToken)
            ?? throw new InvalidOperationException("manifest.json is invalid.");
    }

    public DeveloperAppInfo? FindInstalled(string appId) => _catalog.TryGetValue(appId, out var record)
        ? new DeveloperAppInfo(record.Id, record.DisplayName, record.Version, record.Path)
        : null;

    internal async Task<DeveloperPackageManifest> InspectOfficialDownloadAsync(
        string path, OfficialDesktopRelease release, CancellationToken ct)
    {
        await using var package = File.OpenRead(path);
        await _official.VerifyDownloadAsync(release, package, ct);
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        ValidateOfficialArchive(archive);
        var manifest = await ReadArchiveManifestAsync(archive, ct);
        ValidateManifest(manifest, PackageOrigin.OfficialDistribution);
        RequireReleaseManifest(release, manifest);
        return manifest;
    }

    private static void RequireReleaseManifest(OfficialDesktopRelease release, DeveloperPackageManifest manifest)
    {
        if (manifest.Id != release.AppId || manifest.Version != release.Version)
            throw new InvalidOperationException("component.package_expectation_changed");
    }

    private static void ValidateOfficialArchive(ZipArchive archive)
    {
        const long maximumExpandedBytes = 512L * 1024 * 1024;
        if (archive.Entries.Count > 4096 || archive.Entries.Any(entry => entry.Length > maximumExpandedBytes)
            || archive.Entries.Sum(entry => entry.Length) > maximumExpandedBytes)
            throw new InvalidDataException("The official desktop archive exceeds its allowed size.");
    }

    /// <summary>Loads installed packages at client startup. Invalid old packages are ignored instead of breaking the shell.</summary>
    public async Task LoadInstalledAsync(CancellationToken cancellationToken = default)
    {
        // Startup invokes this from Avalonia's UI thread. Package discovery reads JSON from disk,
        // so it must remain asynchronous; synchronously waiting here prevents the UI dispatcher
        // from running the file I/O continuations and stops the login window from being created.
        await DiscoverExternalPackagesAsync(cancellationToken);
#if !DEBUG
        await MigrateLegacyPackagesAsync(cancellationToken);
#endif
        CleanupStagingDebris();
        CleanupDeferredUninstalls();
        foreach (var record in _catalog.Values.ToArray())
        {
            if (record.PermissionModelVersion != 2)
            {
                _catalog.TryRemove(record.Id, out _);
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
        NotifyPackagesChanged();
    }

    public Task<DeveloperAppInfo> InstallAsync(Stream package, bool launch, CancellationToken cancellationToken = default) =>
        InstallAsync(package, launch, cancellationToken, null);

    internal async Task<DeveloperAppInfo> InstallAsync(Stream package, bool launch, CancellationToken cancellationToken,
        Func<DeveloperPackageManifest, CancellationToken, Task<Guid>>? verifyDeployment)
    {
        await _mutations.WaitAsync(cancellationToken);
        try
        {
            var record = await StageAsync((staging, ct) => ExtractAndReadManifestAsync(package, staging, ct),
                PackageOrigin.LocalPackage, cancellationToken, verifyDeployment);
            if (verifyDeployment is not null)
            {
                try
                {
                    var result = await PromoteAsync(record, launch: false, writePointer: true, cancellationToken);
                    if (!result.Ok) throw new InvalidOperationException(result.Message ?? result.Code);
                    return ToInfo(record);
                }
                finally
                {
                    if (_catalog.GetValueOrDefault(record.Id)?.Path != record.Path)
                        TryDeleteDirectory(record.Path);
                }
            }
            await CommitVersionAsync(record, cancellationToken);
            if (launch && !IsDesktopShellPackage(record.Path)) await LaunchAsync(record.Id);
            return ToInfo(record);
        }
        finally { _mutations.Release(); }
    }

    internal async Task<DeveloperAppInfo> InstallOfficialAsync(Stream package, string path, string appId, string expectedSha256,
        string? installedVersion, CancellationToken cancellationToken, OfficialDesktopRelease? release = null,
        string? installedPath = null, Func<DeveloperPackageManifest, CancellationToken, Task<Guid>>? verifyDeployment = null)
    {
        await _mutations.WaitAsync(cancellationToken);
        try
        {
            if (release is { CatalogUri: not null })
                await _official.VerifyDownloadAsync(release, package, cancellationToken);
            else if (await _official.VerifyAsync(path, appId, package, cancellationToken) != expectedSha256)
                throw new InvalidOperationException("component.package_expectation_changed");
            var installed = FindInstalled(appId);
            if (installed?.Version != installedVersion || release is not null && installed?.InstallationPath != installedPath)
                throw new InvalidOperationException("component.state_revision_conflict");
            if (release is not null && !AppPackages.AppPackageInstallerService.CanInstallRelease(release, installed))
                throw new InvalidOperationException("Choose the installed version or a newer official desktop release.");

            var record = await StageAsync(async (staging, ct) =>
            {
                var manifest = await ExtractAndReadManifestAsync(package, staging, ct, official: true);
                if (manifest.Id != appId)
                    throw new InvalidOperationException("component.package_expectation_changed");
                if (release is not null) RequireReleaseManifest(release, manifest);
                return manifest;
            }, PackageOrigin.OfficialDistribution, cancellationToken, verifyDeployment);
            try
            {
                var result = await PromoteAsync(record, launch: false, writePointer: true, cancellationToken);
                if (!result.Ok) throw new InvalidOperationException(result.Message ?? result.Code);
                return ToInfo(record);
            }
            finally
            {
                if (_catalog.GetValueOrDefault(appId)?.Path != record.Path)
                    TryDeleteDirectory(record.Path);
            }
        }
        finally { _mutations.Release(); }
    }

    private async Task<DeveloperAppRecord> StageAsync(
        Func<string, CancellationToken, Task<DeveloperPackageManifest>> extract, PackageOrigin origin, CancellationToken cancellationToken,
        Func<DeveloperPackageManifest, CancellationToken, Task<Guid>>? verifyDeployment = null)
    {
        var staging = _drive.ResolveUnder(_root, $".staging/{Guid.NewGuid():N}");
        string? destination = null;
        try
        {
            Directory.CreateDirectory(staging);
            var manifest = await extract(staging, cancellationToken);
            ValidateManifest(manifest, origin);
            EnsureReplacementVersionIsCompatible(manifest.Id, manifest.Version.Trim());
            var deploymentReceipt = _drive.ResolveUnder(staging, "deployment-receipt.json");
            if (File.Exists(deploymentReceipt) || Directory.Exists(deploymentReceipt))
                throw new InvalidDataException("The application package uses a reserved deployment receipt path.");
            if (verifyDeployment is not null)
            {
                var planId = await verifyDeployment(manifest, cancellationToken);
                await _drive.WriteJsonAtomicallyAsync(deploymentReceipt, planId, cancellationToken);
            }
            else if (manifest.Dependencies?.Required is { Count: > 0 })
                throw new InvalidOperationException("This package requires joint deployment before installation.");

            // Keep every deployment in a distinct folder. A currently loaded DLL can be locked on
            // Windows, so overwriting a version directory would make the development update flaky.
            destination = VersionPath(manifest.Id, Guid.NewGuid().ToString("N"));
            await _drive.WriteJsonAtomicallyAsync(_drive.ResolveUnder(staging, "app.relaxkonos.json"), ToDescriptor(manifest), cancellationToken);
            if (OfficialDesktopPackages.IsReserved(manifest.Id))
                await _drive.WriteJsonAtomicallyAsync(_drive.ResolveUnder(staging, OfficialDesktopPackages.OriginFileName), origin, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await MoveStagingDirectoryAsync(staging, destination, cancellationToken);
            var record = CreateRecord(destination, manifest, origin);
            destination = null;
            return record;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException
            || IsWindowsStagingLock(exception))
        {
            // Every remaining write of this install lives below the package root: the version folder,
            // the extracted files, the descriptor and the current pointer. A denial here is the same
            // problem as a denial on the staging folder, so name it once for the user.
            RecordActivationDiagnostic($"VSD package storage unavailable: code={VirtualSystemDriveProblemCode.PackageStorageUnavailable}, folder={_root}, error={exception.GetType().Name}.");
            throw new PackageStorageUnavailableException(_root, exception);
        }
        finally
        {
            TryDeleteDirectory(staging);
            if (destination is not null) TryDeleteDirectory(destination);
        }
    }

    private static async Task MoveStagingDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception error) when (attempt < attempts && IsWindowsStagingLock(error))
            {
                // A Windows scanner can briefly hold freshly extracted files without delete sharing.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
            }
        }
    }

    private static bool IsWindowsStagingLock(Exception error) => OperatingSystem.IsWindows()
        && error is IOException or UnauthorizedAccessException
        && (error.HResult & 0xffff) is 5 or 32 or 33; // Access denied, sharing violation, lock violation.

    public Task<bool> UninstallAsync(string appId) => UninstallCoreAsync(appId, null);

    internal Task<bool> UninstallOfficialAsync(DeveloperAppInfo reviewed)
    {
        if (!OfficialDesktopPackages.IsExternalOfficialId(reviewed.Id))
            throw new InvalidOperationException("The application is outside the official desktop store.");
        return UninstallCoreAsync(reviewed.Id, reviewed);
    }

    private async Task<bool> UninstallCoreAsync(string appId, DeveloperAppInfo? reviewed)
    {
        await _mutations.WaitAsync();
        try
        {
            var installed = FindInstalled(appId);
            if (reviewed is not null && installed != reviewed)
                throw new InvalidOperationException("component.state_revision_conflict");
            if (installed is null) return false;
            var requiredBy = RequiredBy(new(DeploymentPackageKind.Application, appId));
            if (requiredBy.Count > 0)
                throw new InvalidOperationException($"The package is required by {string.Join(", ", requiredBy)}.");
            if (!await CanRetireWindowsAsync(new AppId(appId)))
                throw new InvalidOperationException("Application windows are busy or have unsaved changes.");
            File.Delete(CurrentPath(appId));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UnregisterAndUnload(appId);
                _catalog.TryRemove(appId, out _);
                _lastResults.TryRemove(appId, out _);
            });
            // The retired version may still be locked. Startup retries physical cleanup.
            TryDeleteDirectory(AppDirectory(appId));
            NotifyPackagesChanged();
            return true;
        }
        finally { _mutations.Release(); }
    }

    public Task<bool> LaunchAsync(string appId) => Dispatcher.UIThread.InvokeAsync(() => _applications.Launch(new AppId(appId))).GetTask();

    private LoadedDeveloperApp CreateLoaded(DeveloperAppRecord record, bool verifyDependencies = false)
    {
        var assemblyPath = ResolvePackagePath(record.Path, record.EntryAssembly);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("The package entry assembly was not found.", assemblyPath);

        var context = new DeveloperAssemblyLoadContext(assemblyPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            if (verifyDependencies) context.VerifyDependencies(assembly, assemblyPath);
            var type = assembly.GetType(record.EntryType, throwOnError: false)
                ?? throw new InvalidOperationException($"Package entry type '{record.EntryType}' was not found.");
            if (Activator.CreateInstance(type) is not IExternalRemoteApplication application)
                throw new InvalidOperationException("The package entry type must implement IExternalRemoteApplication.");

            if (verifyDependencies && HasFileHandlers(record) && application is not IExternalFileOpenApplication)
                throw new InvalidOperationException("The manifest declares file handlers but its entry type does not implement IExternalFileOpenApplication.");
            if (verifyDependencies && record.SupportedUriSchemes is { Count: > 0 } && application is not IExternalAppActivationHandler)
                throw new InvalidOperationException("The manifest declares URI schemes but its entry type does not implement IExternalAppActivationHandler.");

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
        if (_applications.IsBuiltIn(new AppId(record.Id)))
            throw new InvalidOperationException("The application id is already registered by the host.");
        UnregisterAndUnload(record.Id);
        if (IsDesktopShellPackage(record.Path)) return;
        _applications.Register(CreateAdapter(record));
    }

    private ExternalApplicationAdapter CreateAdapter(DeveloperAppRecord record) =>
        HasFileHandlers(record)
            ? new ExternalFileApplicationAdapter(record, this, _contextFactory)
            : new ExternalApplicationAdapter(record, this, _contextFactory);

    private static bool HasFileHandlers(DeveloperAppRecord record) =>
        record.SupportedFileExtensions.Count > 0 || record.SupportedFileNames is { Count: > 0 }
        || record.SupportsExtensionlessFiles || record.SupportsTextFiles;

    private Task<bool> CanRetireWindowsAsync(AppId id) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var owned = _windowManager.Windows.Where(window => window.Info.OwnerAppId == id).ToArray();
        foreach (var window in owned)
        {
            if (window.CanCloseAsync is { } canClose
                && (_windowManager.Windows.Any(dialog => dialog.Info.OwnerAppId == id && dialog.IsModalDialog) || !await canClose()))
                return false;
        }
        return true;
    });

    private void UnregisterAndUnload(string appId)
    {
        var id = new AppId(appId);
        (_applications.Get(id) as ExternalApplicationAdapter)?.Retire();
        CloseWindows(id);
        _applications.Unregister(id);
    }

    private void CloseWindows(AppId id, Func<ManagedWindow, bool>? predicate = null)
    {
        foreach (var window in _windowManager.Windows.Where(window => window.Info.OwnerAppId == id).ToArray())
        {
            if (predicate is not null && !predicate(window)) continue;
            try { _windowManager.ForceClose(window); }
            catch (Exception exception) { RecordActivationDiagnostic($"Window close failed: app={id.Value}, error={exception.GetType().Name}."); }
        }
    }

    private async Task RestoreFallbackAsync(DeveloperAppRecord failed)
    {
        await _mutations.WaitAsync();
        try
        {
            if (!_catalog.TryGetValue(failed.Id, out var current) || !ReferenceEquals(current, failed)) return;
            var pointer = await ReadPointerAsync(failed.Id, default);
            if (pointer.PreviousVersionId is not { } previous) return;
            var fallback = await ReadVersionAsync(failed.Id, previous, default);
            await CommitVersionAsync(fallback, default, rememberPrevious: false);
            RecordActivationDiagnostic($"VSD package rollback: app={fallback.Id}, result=previous-version-restored.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or VirtualSystemDriveException)
        {
            RecordActivationDiagnostic($"VSD package rollback: app={SafeDiagnosticId(failed.Id)}, code={VirtualSystemDriveProblemCode.PackageLayoutInvalid}.");
        }
        finally { _mutations.Release(); }
    }

    internal void RecordActivationDiagnostic(string message) => _activationDiagnostics.Record(message);

    private static ApplicationManifest ToApplicationManifest(DeveloperAppRecord record) => new(
        new AppId(record.Id), record.DisplayName, record.Version, record.IconGlyph, record.Description,
        record.RequestedPermissions, record.SupportedFileExtensions, record.LocalizedMetadata,
        record.ClientPlatforms, record.ServerRequirements, record.SupportedFileNames, record.SupportsExtensionlessFiles,
        SupportsTextFiles: record.SupportsTextFiles,
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
        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(_root).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordActivationDiagnostic($"VSD package discovery unavailable: code=vsd-package-root-unreadable, error={exception.GetType().Name}.");
            return;
        }
        foreach (var rawDirectory in directories)
        {
            var appId = Path.GetFileName(rawDirectory);
            if (appId.Equals(".staging", StringComparison.Ordinal) || !ApplicationDescriptorValidator.IsValidAppId(appId))
                continue;
            try
            {
                var current = await ReadPointerAsync(appId, cancellationToken);
                _catalog[appId] = await ReadVersionAsync(appId, current.VersionId, cancellationToken);
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
                if (OfficialDesktopPackages.IsReserved(legacy.Id)) throw new InvalidOperationException();
                if (legacy.PermissionModelVersion != 2 || !Directory.Exists(legacy.Path)) throw new InvalidOperationException();
                var legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_legacyRoot)) + Path.DirectorySeparatorChar;
                var source = Path.GetFullPath(legacy.Path);
                if (!source.StartsWith(legacyRoot, StringComparison.Ordinal)) throw new InvalidOperationException();

                var versionId = $"legacy-{Guid.NewGuid():N}";
                var destination = VersionPath(legacy.Id, versionId);
                CopyDirectoryWithoutLinks(source, destination);
                var migrated = legacy with { Path = destination, IconPath = null };
                await _drive.WriteJsonAtomicallyAsync(_drive.ResolveUnder(destination, "app.relaxkonos.json"), ToDescriptor(migrated), cancellationToken);
                await _drive.WriteJsonAtomicallyAsync(CurrentPath(migrated.Id), new ExternalCurrentVersion(1, migrated.Id, versionId), cancellationToken);
                _catalog[migrated.Id] = migrated;
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

    private async Task<DeveloperPackageManifest> ExtractAndReadManifestAsync(Stream package, string destination,
        CancellationToken cancellationToken, bool official = false)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        if (official) ValidateOfficialArchive(archive);
        var manifest = await ReadArchiveManifestAsync(archive, cancellationToken);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            var target = ResolvePayloadFile(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew);
            await source.CopyToAsync(output, cancellationToken);
        }

        return manifest;
    }

    private static void ValidateManifest(DeveloperPackageManifest manifest, PackageOrigin origin)
    {
        ValidateAppId(manifest.Id);
        if (OfficialDesktopPackages.IsReserved(manifest.Id)
            && (!OfficialDesktopPackages.IsAllowed(manifest.Id, origin) || manifest.PackageType == "desktopShell"))
            throw new InvalidOperationException("The relaxkonos.* application id range requires a host-approved application source.");
        if (manifest.PermissionModelVersion != 2)
            throw new InvalidOperationException("This package uses an unsupported permission model. Rebuild it with permissionModelVersion: 2.");
        if (manifest.PackageType is not null and not "application" and not "desktopShell")
            throw new InvalidOperationException("packageType must be application or desktopShell.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new InvalidOperationException("manifest.json is missing a required field.");
        if (manifest.Dependencies is { } dependencies)
        {
            ValidateDependencies(dependencies.Required, optional: false);
            ValidateDependencies(dependencies.Optional, optional: true);
            if (manifest.PackageType == "desktopShell" || !RuntimeSemanticVersion.TryParse(manifest.Version, out _))
                throw new InvalidDataException("Deployment dependencies require an application package with a semantic version.");
        }
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
                || scheme.Equals("relaxkonos", StringComparison.OrdinalIgnoreCase)) == true)
            throw new InvalidOperationException("supportedUriSchemes must contain non-reserved URI schemes.");
        _ = ParseInstancePolicy(manifest.InstancePolicy);
    }

    private static void ValidateDependencies(IReadOnlyList<DeploymentRequirement>? requirements, bool optional)
    {
        if (requirements is null) return;
        var keys = new HashSet<DeploymentPackageKey>();
        if (requirements.Count > 128)
            throw new InvalidDataException("Too many deployment dependencies.");
        foreach (var requirement in requirements)
        {
            if (requirement is null || !Enum.IsDefined(requirement.Package.Kind)
                || optional && requirement.Package.Kind != DeploymentPackageKind.ServerModule
                || requirement.Package.Id is not { Length: > 0 and <= 128 } id
                || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
                || requirement.Capability is { Length: 0 or > 128 }
                || requirement.ContractVersion is { Length: 0 or > 128 }
                || requirement.ContractVersion is not null && requirement.Capability is null
                || !keys.Add(requirement.Package))
                throw new InvalidDataException("Invalid deployment dependency.");

            RuntimeSemanticVersion? minimum = null;
            RuntimeSemanticVersion? maximum = null;
            if (requirement.MinimumVersion is { } lower && !RuntimeSemanticVersion.TryParse(lower, out minimum)
                || requirement.MaximumVersionExclusive is { } upper && !RuntimeSemanticVersion.TryParse(upper, out maximum)
                || minimum is not null && maximum is not null && minimum.CompareTo(maximum) >= 0)
                throw new InvalidDataException("Invalid deployment dependency version range.");
        }
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
        return Enum.TryParse<ApplicationInstancePolicy>(value, ignoreCase: true, out var policy) && Enum.IsDefined(policy)
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

    /// <summary>
    /// Removes staging folders abandoned by an installation that the process never finished.
    /// </summary>
    /// <remarks>
    /// Each attempt uses its own <c>.staging/&lt;id&gt;</c> folder and deletes it in a <c>finally</c>, so
    /// anything still there at startup belongs to a crashed or killed client. Left alone they would
    /// accumulate unpacked packages forever, and a read-only root must not turn this into a failure.
    /// </remarks>
    private void CleanupStagingDebris()
    {
        string staging;
        string[] directories;
        try
        {
            staging = _drive.ResolveUnder(_root, ".staging");
            if (!Directory.Exists(staging)) return;
            directories = Directory.EnumerateDirectories(staging).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or VirtualSystemDriveException)
        {
            RecordActivationDiagnostic($"VSD staging cleanup unavailable: error={exception.GetType().Name}.");
            return;
        }
        foreach (var directory in directories) TryDeleteDirectory(directory);
    }

    /// <summary>Removes package directories left behind by a prior successful logical uninstall.</summary>
    private void CleanupDeferredUninstalls()
    {
        if (!Directory.Exists(_root)) return;
        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(_root).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordActivationDiagnostic($"VSD deferred cleanup unavailable: code=vsd-package-root-unreadable, error={exception.GetType().Name}.");
            return;
        }
        foreach (var directory in directories)
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
        if (!ApplicationDescriptorValidator.IsValidAppId(appId) || appId.EndsWith('.') || IsWindowsDeviceName(appId))
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

    /// <summary>
    /// The external-package catalog is rebuilt from fixed <c>current.json</c> pointers and validated
    /// descriptors on every startup, so it is a cache. A read-only or sandboxed VSD may deny this
    /// write; keep the in-memory registry usable and let the next startup rebuild the file.
    /// </summary>
    private void SaveCatalog(string path, IReadOnlyDictionary<string, DeveloperAppRecord> catalog)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            RecordActivationDiagnostic($"VSD external catalog not written: code=vsd-external-catalog-unavailable, error={exception.GetType().Name}.");
        }
    }

    private sealed class DeveloperAssemblyLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "RelaxKonOS.App.SDK", "RelaxKonOS.Core", "RelaxKonOS.WindowManager", "RelaxKonOS.UI",
            "RelaxKonOS.Protocol", "RelaxKonOS.Shell",
            "AvaloniaEdit",
        };
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not { } name || SharedAssemblies.Contains(name)
                || name == "Avalonia" || name.StartsWith("Avalonia.", StringComparison.Ordinal))
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null && !name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                var sibling = Path.Combine(Path.GetDirectoryName(mainAssemblyPath)!, name + ".dll");
                if (File.Exists(sibling)) path = sibling;
            }
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }

        public void VerifyDependencies(Assembly entry, string entryPath)
        {
            var visited = new HashSet<Assembly>();
            Visit(entry);

            var dependencyFile = Path.ChangeExtension(entryPath, ".deps.json");
            if (!File.Exists(dependencyFile)) return;
            using var deps = JsonDocument.Parse(File.ReadAllText(dependencyFile));
            var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            foreach (var library in deps.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
            {
                if (library.Value.TryGetProperty("runtime", out var runtime))
                {
                    foreach (var asset in runtime.EnumerateObject())
                    {
                        if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        var name = Path.GetFileNameWithoutExtension(asset.Name);
                        if (name != entry.GetName().Name) Visit(LoadFromAssemblyName(new AssemblyName(name)));
                    }
                }
                if (library.Value.TryGetProperty("native", out var native))
                {
                    foreach (var asset in native.EnumerateObject())
                    {
                        var name = Path.GetFileName(asset.Name);
                        if (name != "_._" && _resolver.ResolveUnmanagedDllToPath(name) is null
                            && !File.Exists(Path.Combine(Path.GetDirectoryName(entryPath)!, name)))
                            throw new FileNotFoundException($"Native dependency '{name}' was not found.");
                    }
                }
            }

            void Visit(Assembly assembly)
            {
                if (GetLoadContext(assembly) != this || !visited.Add(assembly)) return;
                foreach (var dependency in assembly.GetReferencedAssemblies()) Visit(LoadFromAssemblyName(dependency));
            }
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
        private readonly CancellationTokenSource _lifetime = new();
        private LoadedDeveloperApp? _loaded;
        private HashSet<ManagedWindow>? _observedWindows;

        public ExternalApplicationAdapter(DeveloperAppRecord record, DeveloperPackageManager owner, ExternalAppContextFactory contextFactory)
        {
            Record = record;
            Owner = owner;
            ContextFactory = contextFactory;
        }

        public override ApplicationManifest Manifest => ToApplicationManifest(Record);
        internal bool IsLoaded => _loaded is not null;
        protected CancellationToken Lifetime => _lifetime.Token;

        internal void ValidateLoad() => _loaded ??= Owner.CreateLoaded(Record, verifyDependencies: true);

        internal void Retire()
        {
            try { _lifetime.Cancel(); }
            catch (AggregateException exception)
            {
                Owner.RecordActivationDiagnostic($"App cancellation failed: app={Record.Id}, error={exception.GetType().Name}.");
            }
            _loaded?.LoadContext.Unload();
            _loaded = null;
        }

        protected IExternalAppContext CreateContext(string? openedFile = null) => ContextFactory.Create(Manifest, _lifetime.Token,
            _observedWindows is { } windows ? window => windows.Add(window) : null, openedFile);

        protected IExternalRemoteApplication GetOrLoad()
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            try { return (_loaded ??= Owner.CreateLoaded(Record)).Application; }
            catch
            {
                _ = Owner.RestoreFallbackAsync(Record);
                throw;
            }
        }

        internal async Task ActivateObservedAsync(HashSet<ManagedWindow> windows, CancellationToken cancellationToken)
        {
            _observedWindows = windows;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            try { await GetOrLoad().ActivateAsync(CreateContext(), linked.Token).WaitAsync(linked.Token); }
            finally { _observedWindows = null; }
        }

        public override void Activate(AppContext context) => _ = ActivateAsync(context);

        protected async Task ActivateAsync(AppContext context)
        {
            try
            {
                await GetOrLoad().ActivateAsync(CreateContext(), _lifetime.Token);
            }
            catch (Exception exception)
            {
                Owner.RecordActivationDiagnostic($"External app launch failed: app={Manifest.Id.Value}, error={exception.GetType().Name}: {exception.Message}");
                ShowFailure(context, exception);
                if (Record.Origin == PackageOrigin.OfficialDistribution && !_lifetime.IsCancellationRequested)
                    _ = Owner.RestoreFallbackAsync(Record);
            }
        }

        protected void ShowFailure(AppContext context, Exception exception)
        {
            if (_lifetime.IsCancellationRequested) return;
            context.ShowWindow($"{Manifest.DisplayName} failed to start",
                new TextBlock { Text = exception.Message, Margin = new Avalonia.Thickness(20), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                iconGlyph: Manifest.IconGlyph, canResize: true);
        }

        public bool CanHandleActivation(Uri uri)
        {
            if (!Manifest.UriSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
                return false;
            try
            {
                return GetOrLoad() is IExternalAppActivationHandler handler
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
                if (GetOrLoad() is IExternalAppActivationHandler handler)
                    await handler.HandleActivationAsync(CreateContext(), request.Uri, _lifetime.Token);
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
                await ((IExternalFileOpenApplication)GetOrLoad())
                    .OpenFileAsync(CreateContext(path), path, Lifetime);
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
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
    string? Sha256 = null,
    bool SupportsTextFiles = false,
    PackageDeploymentDependencies? Dependencies = null);

public sealed record PackageDeploymentDependencies(
    IReadOnlyList<DeploymentRequirement>? Required = null,
    IReadOnlyList<DeploymentRequirement>? Optional = null);

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
    int PermissionModelVersion = 0,
    PackageOrigin Origin = PackageOrigin.LocalPackage,
    bool SupportsTextFiles = false,
    PackageDeploymentDependencies? Dependencies = null);

internal sealed record ExternalCurrentVersion(int SchemaVersion, string AppId, string VersionId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? PreviousVersionId = null);

public sealed record DeveloperAppInfo(string Id, string DisplayName, string Version, string InstallationPath);
