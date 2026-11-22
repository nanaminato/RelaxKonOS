using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Text.Json;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.VirtualSystemDrive;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Services.Developer;

public sealed partial class DeveloperPackageManager
{
    private const long MaxDirectBytes = 1024L * 1024 * 1024;
    private const int MaxDirectFiles = 20000;
    private readonly ConcurrentDictionary<string, DeveloperOperationResult> _lastResults = new(StringComparer.Ordinal);

    internal async Task<DeveloperOperationResult> DeployAsync(Stream payload, bool launch, CancellationToken cancellationToken)
    {
        await _mutations.WaitAsync(cancellationToken);
        try
        {
            DeveloperAppRecord record;
            try { record = await StageAsync(ExtractDirectAsync, PackageOrigin.DirectDeploy, cancellationToken); }
            catch (PackageStorageUnavailableException exception)
            {
                return new(exception.ProblemCode, Message: exception.Message);
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
            {
                return new("invalid_payload", Message: exception.Message);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            {
                return new("invalid_manifest", Message: exception.Message);
            }

            try { return await PromoteAsync(record, launch, writePointer: true, cancellationToken); }
            finally
            {
                if (!_catalog.TryGetValue(record.Id, out var current) || current.Path != record.Path)
                    TryDeleteDirectory(record.Path);
            }
        }
        finally { _mutations.Release(); }

        async Task<DeveloperPackageManifest> ExtractDirectAsync(string staging, CancellationToken ct)
        {
            using var reader = new TarReader(payload, leaveOpen: true);
            long total = 0;
            var count = 0;
            var buffer = new byte[81920];
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || ++count > MaxDirectFiles || entry.Length > MaxDirectBytes - total)
                    throw new InvalidDataException("Direct Deploy accepts up to 20,000 regular files and 1 GiB of file data.");
                total += entry.Length;
                var target = ResolvePayloadFile(staging, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) throw new InvalidDataException("The upload contains a duplicate package path.");
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                // TarReader exposes a bounded stream. Copy exactly the advertised bytes so it
                // never issues a zero-length read against HttpListener's non-seekable input.
                var remaining = entry.Length;
                while (remaining > 0)
                {
                    var chunk = buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining));
                    await entry.DataStream!.ReadExactlyAsync(chunk, ct);
                    await output.WriteAsync(chunk, ct);
                    remaining -= chunk.Length;
                }
            }

            var manifest = await _drive.ReadJsonAsync<DeveloperPackageManifest>(_drive.ResolveUnder(staging, "manifest.json"), ct);
            if (manifest.PackageType == "desktopShell")
                throw new InvalidOperationException("Use the .roapp installer for desktop shell packages.");
            return manifest;
        }
    }

    private string ResolvePayloadFile(string staging, string path)
    {
        if (!ApplicationDescriptorValidator.IsSafeRelativePath(path)
            || path.Split('/').Any(segment => segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsWindowsDeviceName(segment))
            || path.Equals("app.relaxkonos.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals(OfficialDesktopPackages.OriginFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The upload contains an invalid package path.");
        return _drive.ResolveUnder(staging, path);
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var name = segment.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && "0123456789¹²³".Contains(name[3]));
    }

    internal async Task<DeveloperOperationResult> SwitchAsync(string appId, bool rollback, bool launch, CancellationToken cancellationToken)
    {
        ValidateAppId(appId);
        await _mutations.WaitAsync(cancellationToken);
        try
        {
            if (!_catalog.TryGetValue(appId, out var current)) return new("app_not_found", appId);
            if (IsDesktopShellPackage(current.Path)) return new("unsupported_package_type", appId);
            if (!rollback) return await PromoteAsync(current, launch, writePointer: false, cancellationToken);

            var pointer = await ReadPointerAsync(appId, cancellationToken);
            if (pointer.PreviousVersionId is not { } previous) return new("rollback_unavailable", appId);
            var record = await ReadVersionAsync(appId, previous, cancellationToken);
            if (IsDesktopShellPackage(record.Path)) return new("unsupported_package_type", appId);
            return await PromoteAsync(record, launch, writePointer: true, cancellationToken, restoreDependency: true);
        }
        finally { _mutations.Release(); }
    }

    internal async Task<IReadOnlyList<DeveloperAppStatus>> StatusAsync(string? appId, CancellationToken cancellationToken)
    {
        await _mutations.WaitAsync(cancellationToken);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => _catalog.Values
                .Where(app => appId is null || app.Id == appId).OrderBy(app => app.Id, StringComparer.Ordinal)
                .Select(app =>
                {
                    var id = new AppId(app.Id);
                    var registered = _applications.Get(id) as ExternalApplicationAdapter;
                    var windows = _windowManager.Windows.Where(window => window.Info.OwnerAppId == id && !window.IsModalDialog)
                        .Select(window => window.Info.Title).ToArray();
                    return new DeveloperAppStatus(app.Id, app.Version, VersionId(app), registered is not null,
                        registered?.IsLoaded == true, windows, _lastResults.GetValueOrDefault(app.Id));
                }).ToArray());
        }
        finally { _mutations.Release(); }
    }

    private async Task<DeveloperOperationResult> PromoteAsync(DeveloperAppRecord record, bool launch, bool writePointer,
        CancellationToken cancellationToken, bool restoreDependency = false)
    {
        _catalog.TryGetValue(record.Id, out var previous);
        var id = new AppId(record.Id);
        var result = new DeveloperOperationResult("ok", record.Id, VersionId(record), previous is null ? null : VersionId(previous));
        var candidate = CreateAdapter(record);
        var candidateWindows = new HashSet<ManagedWindow>();
        IRemoteApplication? priorRegistration = null;
        Action? restorePermissions = null;
        var swapped = false;
        var committed = false;
        var stageCode = "incompatible_client";
        try
        {
            if (writePointer)
            {
                stageCode = "dependency_version_conflict";
                EnsureReplacementVersionIsCompatible(record.Id, record.Version);
                stageCode = "incompatible_client";
            }
            if (record.ClientPlatforms is { Count: > 0 }
                && !ToApplicationManifest(record).SupportedClientPlatforms.Contains(ApplicationCompatibilityService.DetectClientPlatform(), StringComparer.Ordinal))
                throw new InvalidOperationException("This application does not support the current client platform.");
            stageCode = "assembly_load_failed";
            await Dispatcher.UIThread.InvokeAsync(() => candidate.ValidateLoad());
            result = result with { AssemblyLoad = "passed" };
            cancellationToken.ThrowIfCancellationRequested();
            stageCode = "close_blocked";
            if (previous is not null && !await CanRetireWindowsAsync(id).WaitAsync(cancellationToken))
                throw new InvalidOperationException("Application windows are busy or have unsaved changes.");
            cancellationToken.ThrowIfCancellationRequested();
            if (restoreDependency)
            {
                stageCode = "dependency_unavailable";
                await RestoreDesktopDependencyAsync(record, cancellationToken);
            }
            stageCode = "registration_failed";
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                priorRegistration = _applications.Get(id);
                if (priorRegistration is not null and not ExternalApplicationAdapter)
                    throw new InvalidOperationException("The application id is already registered by the host.");
                swapped = true;
                if (writePointer)
                {
                    if (_permissions is JsonAppPermissionManager grants) restorePermissions = grants.ResetForPackageUpdate(id);
                    else _permissions.Clear(id);
                }
                _applications.Register(candidate);
            });

            if (launch)
            {
                stageCode = "activation_failed";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var launched = await Dispatcher.UIThread.InvokeAsync(async () =>
                    _windowManager.HostBounds.Width > 0 && _windowManager.HostBounds.Height > 0
                    && await _applications.LaunchObservedAsync(id, () => candidate.ActivateObservedAsync(candidateWindows, timeout.Token), timeout.Token));
                if (!launched)
                {
                    stageCode = "launch_blocked";
                    throw new InvalidOperationException("Application launch requires an open, compatible desktop workspace.");
                }
                var opened = await Dispatcher.UIThread.InvokeAsync(() => _windowManager.Windows.Any(window =>
                    candidateWindows.Contains(window) && !window.IsModalDialog));
                result = result with { WindowLaunch = opened ? "passed" : "notOpened" };
            }

            stageCode = "storage_unavailable";
            if (writePointer) await WritePointerAsync(record, previous, cancellationToken);
            committed = true;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _catalog[record.Id] = record;
                (priorRegistration as ExternalApplicationAdapter)?.Retire();
                CloseWindows(id, window => !candidateWindows.Contains(window));
                if (launch) _applications.RequestUndecidedPermissions(id);
            });
            NotifyPackagesChanged();
            if (writePointer) await DrainDesktopDependencyReleasesAsync();
            result = result with { Installation = writePointer ? "passed" : "unchanged", CurrentVersionId = VersionId(record) };
        }
        catch (Exception exception) when (!committed)
        {
            var code = exception is OperationCanceledException
                ? cancellationToken.IsCancellationRequested ? "cancelled" : "activation_timeout"
                : stageCode;
            result = result with
            {
                Code = code,
                Installation = writePointer ? "failed" : "unchanged",
                AssemblyLoad = stageCode == "assembly_load_failed" ? "failed" : result.AssemblyLoad,
                WindowLaunch = stageCode is "activation_failed" or "launch_blocked" ? "failed" : result.WindowLaunch,
                Message = exception.GetBaseException().Message,
            };
            RecordActivationDiagnostic($"Developer package operation: app={record.Id}, code={code}, error={exception.GetType().Name}.");
        }
        finally
        {
            if (!committed)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    candidate.Retire();
                    CloseWindows(id, candidateWindows.Contains);
                    if (!swapped) return;
                    if (priorRegistration is null) _applications.Unregister(id);
                    else _applications.Register(priorRegistration);
                    try { restorePermissions?.Invoke(); }
                    catch (Exception exception)
                    {
                        RecordActivationDiagnostic($"Package grant restoration failed: app={record.Id}, error={exception.GetType().Name}.");
                        result = result with { Code = "permission_restore_failed", Message = exception.Message };
                    }
                });
                if (restoreDependency) await DrainDesktopDependencyReleasesAsync();
            }
        }
        _lastResults[record.Id] = result;
        return result;
    }

    private async Task CommitVersionAsync(DeveloperAppRecord record, CancellationToken cancellationToken, bool rememberPrevious = true)
    {
        EnsureReplacementVersionIsCompatible(record.Id, record.Version);
        var previous = rememberPrevious ? _catalog.GetValueOrDefault(record.Id) : null;
        await WritePointerAsync(record, previous, cancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Register(record);
            _permissions.Clear(new AppId(record.Id));
            _catalog[record.Id] = record;
            _lastResults.TryRemove(record.Id, out _);
        });
        NotifyPackagesChanged();
        await DrainDesktopDependencyReleasesAsync();
    }

    private void NotifyPackagesChanged()
    {
        SaveCatalog(_catalogPath, _catalog);
        void Notify()
        {
            if (PackagesChanged is not { } handlers) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception exception) { RecordActivationDiagnostic($"Package change notification failed: {exception.GetType().Name}."); }
            }
        }
        if (Dispatcher.UIThread.CheckAccess()) Notify();
        else Dispatcher.UIThread.Post(Notify);
    }

    private async Task WritePointerAsync(DeveloperAppRecord record, DeveloperAppRecord? previous, CancellationToken cancellationToken)
    {
        if (_catalog.GetValueOrDefault(record.Id) is { } active && active.Path != record.Path)
            await QueueDesktopDependencyReleaseAsync(ToInfo(active));
        await _drive.WriteJsonAtomicallyAsync(CurrentPath(record.Id), new ExternalCurrentVersion(1, record.Id, VersionId(record),
            previous is null ? null : VersionId(previous)), cancellationToken);
    }

    private async Task<ExternalCurrentVersion> ReadPointerAsync(string appId, CancellationToken cancellationToken)
    {
        var pointer = await _drive.ReadJsonAsync<ExternalCurrentVersion>(CurrentPath(appId), cancellationToken);
        if (pointer.SchemaVersion != 1 || pointer.AppId != appId || !IsVersionId(pointer.VersionId)
            || (pointer.PreviousVersionId is not null && (!IsVersionId(pointer.PreviousVersionId) || pointer.PreviousVersionId == pointer.VersionId)))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PackageLayoutInvalid);
        return pointer;
    }

    private async Task<DeveloperAppRecord> ReadVersionAsync(string appId, string versionId, CancellationToken cancellationToken)
    {
        var root = VersionPath(appId, versionId);
        var descriptor = await _drive.ReadJsonAsync<ApplicationDescriptor>(_drive.ResolveUnder(root, "app.relaxkonos.json"), cancellationToken);
        var manifest = await _drive.ReadJsonAsync<DeveloperPackageManifest>(_drive.ResolveUnder(root, "manifest.json"), cancellationToken);
        var origin = OfficialDesktopPackages.IsReserved(appId)
            ? await _drive.ReadJsonAsync<PackageOrigin>(_drive.ResolveUnder(root, OfficialDesktopPackages.OriginFileName), cancellationToken)
            : PackageOrigin.LocalPackage;
        ValidateManifest(manifest, origin);
        if (!ApplicationDescriptorValidator.Validate(descriptor).IsValid || descriptor.Kind != ApplicationDescriptorKind.Package
            || manifest.Id != appId || descriptor.Id != appId || descriptor.Activation.EntryAssembly != manifest.EntryAssembly.Trim()
            || descriptor.Activation.EntryType != manifest.EntryType.Trim())
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PackageLayoutInvalid);
        return CreateRecord(root, manifest, origin);
    }

    private static DeveloperAppRecord CreateRecord(string root, DeveloperPackageManifest manifest, PackageOrigin origin) => new(
        manifest.Id, manifest.DisplayName.Trim(), manifest.Version.Trim(), root, manifest.EntryAssembly.Trim(), manifest.EntryType.Trim(),
        manifest.IconGlyph, ResolveIconPath(root, manifest.IconPath), manifest.Description, manifest.RequestedPermissions ?? [],
        manifest.SupportedFileExtensions ?? [], manifest.LocalizedMetadata, manifest.ClientPlatforms ?? [], manifest.ServerRequirements,
        manifest.SupportedFileNames ?? [], manifest.SupportsExtensionlessFiles, ParseInstancePolicy(manifest.InstancePolicy),
        manifest.SupportedUriSchemes ?? [], manifest.PermissionModelVersion, origin, manifest.SupportsTextFiles,
        manifest.Dependencies);

    private static DeveloperAppInfo ToInfo(DeveloperAppRecord record) => new(record.Id, record.DisplayName, record.Version, record.Path);
    private static string VersionId(DeveloperAppRecord record) => Path.GetFileName(record.Path);
}

internal sealed record DeveloperOperationResult(string Code, string? AppId = null, string? AttemptedVersionId = null,
    string? CurrentVersionId = null, string Installation = "notRun", string AssemblyLoad = "notRun",
    string WindowLaunch = "notRun", string UiTest = "notRun", string? Message = null)
{
    public bool Ok => Code == "ok";
}

internal sealed record DeveloperAppStatus(string AppId, string Version, string CurrentVersionId, bool Registered, bool AssemblyLoaded,
    IReadOnlyList<string> Windows, DeveloperOperationResult? LastResult);
