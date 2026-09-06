using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.VirtualSystemDrive;
using RemoteOS.Runtime;

namespace Client.Services.VirtualSystemDrive;

/// <summary>
/// Discovers the fixed-depth, Host-owned built-in catalog. Disk descriptors are evidence that an
/// installation mirror is intact; they never choose code, identity, or authorization.
/// </summary>
public sealed class ApplicationCatalogScanner
{
    private const int CatalogSchemaVersion = 1;
    private readonly VirtualSystemDrive _drive;
    private readonly BuiltInDescriptorSeeder _seeder;
    private readonly IBuiltInApplicationFactoryRegistry _builtIns;
    private readonly IServiceProvider _services;
    private readonly IAppActivationDiagnostics _diagnostics;

    public ApplicationCatalogScanner(
        VirtualSystemDrive drive,
        BuiltInDescriptorSeeder seeder,
        IBuiltInApplicationFactoryRegistry builtIns,
        IServiceProvider services,
        IAppActivationDiagnostics diagnostics)
    {
        _drive = drive;
        _seeder = seeder;
        _builtIns = builtIns;
        _services = services;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Repairs the Host descriptor mirror, scans it, persists a disposable index, then uses the
    /// existing ApplicationManager API as the only runtime registration path.
    /// </summary>
    public async Task<ApplicationCatalogScanResult> ScanAndRegisterBuiltInsAsync(
        ApplicationManager applications,
        CancellationToken cancellationToken = default)
    {
        await _seeder.EnsureSeededAsync(cancellationToken);
        var result = await ScanBuiltInsAsync(cancellationToken);
        foreach (var problem in result.Problems)
            _diagnostics.Record($"VSD catalog rejected: app={problem.AppId ?? "<unknown>"}, source={problem.Source}, code={problem.ProblemCode}.");

        foreach (var entry in result.Entries)
        {
            // The descriptor has already been matched to this exact compiled definition. The
            // factory and manifest remain Host-owned even after a descriptor was modified.
            applications.RegisterBuiltIn(entry.Definition.Factory(_services));
        }

        await WriteCacheAsync(result, cancellationToken);
        return result;
    }

    public async Task<ApplicationCatalogScanResult> ScanBuiltInsAsync(CancellationToken cancellationToken = default)
    {
        _drive.EnsureCreated();
        var entries = new List<ApplicationCatalogEntry>();
        var problems = new List<ApplicationCatalogProblem>();
        var ids = new HashSet<AppId>();

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_drive.BuiltInProgramsDirectory).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add(new ApplicationCatalogProblem(null, "Programs/BuiltIn", VirtualSystemDriveProblemCode.PathInvalid));
            return new ApplicationCatalogScanResult(entries, problems);
        }

        foreach (var candidate in directories.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(candidate);
            var source = $"Programs/BuiltIn/{directoryName}";
            if (!ApplicationDescriptorValidator.IsValidAppId(directoryName))
            {
                problems.Add(new ApplicationCatalogProblem(null, source, VirtualSystemDriveProblemCode.AppIdInvalid));
                continue;
            }

            try
            {
                var directory = _drive.ResolveUnder(_drive.BuiltInProgramsDirectory, directoryName);
                var descriptorPath = _drive.ResolveUnder(directory, "app.remoteos.json");
                var descriptor = await _drive.ReadJsonAsync<ApplicationDescriptor>(descriptorPath, cancellationToken);
                var validation = ApplicationDescriptorValidator.Validate(descriptor);
                if (!validation.IsValid)
                {
                    problems.Add(new ApplicationCatalogProblem(SafeAppId(descriptor.Id), source, validation.ProblemCode!));
                    continue;
                }
                if (descriptor.Kind != ApplicationDescriptorKind.BuiltIn
                    || descriptor.Id != directoryName
                    || !_builtIns.TryGet(descriptor.Activation.BuiltInKey!, out var definition)
                    || definition.AppId.Value != descriptor.Id
                    || !BuiltInDescriptorSeeder.MatchesDefinition(descriptor, definition))
                {
                    problems.Add(new ApplicationCatalogProblem(SafeAppId(descriptor.Id), source, VirtualSystemDriveProblemCode.BuiltInMismatch));
                    continue;
                }
                if (!ids.Add(definition.AppId))
                {
                    problems.Add(new ApplicationCatalogProblem(definition.AppId.Value, source, VirtualSystemDriveProblemCode.DuplicateAppId));
                    continue;
                }

                entries.Add(new ApplicationCatalogEntry(definition.AppId.Value, source, definition));
            }
            catch (VirtualSystemDriveException exception)
            {
                problems.Add(new ApplicationCatalogProblem(null, source, exception.ProblemCode));
            }
            catch (IOException)
            {
                problems.Add(new ApplicationCatalogProblem(null, source, VirtualSystemDriveProblemCode.PathInvalid));
            }
            catch (UnauthorizedAccessException)
            {
                problems.Add(new ApplicationCatalogProblem(null, source, VirtualSystemDriveProblemCode.PathInvalid));
            }
        }

        return new ApplicationCatalogScanResult(entries, problems);
    }

    private async Task WriteCacheAsync(ApplicationCatalogScanResult result, CancellationToken cancellationToken)
    {
        var path = _drive.ResolveRootChild("System/catalog.json");
        var cache = new ApplicationCatalogCache(
            CatalogSchemaVersion,
            DateTimeOffset.UtcNow,
            result.Entries.Select(entry => new ApplicationCatalogCacheEntry(entry.AppId, "builtin", entry.Source)).ToArray());
        await _drive.WriteJsonAtomicallyAsync(path, cache, cancellationToken);
    }

    private static string? SafeAppId(string? value) => ApplicationDescriptorValidator.IsValidAppId(value) ? value : null;
}

/// <summary>A redacted catalog issue: it never includes an absolute VSD path, document text, or exception details.</summary>
public sealed record ApplicationCatalogProblem(string? AppId, string Source, string ProblemCode);

public sealed record ApplicationCatalogEntry(string AppId, string Source, BuiltInApplicationDefinition Definition);

public sealed record ApplicationCatalogScanResult(
    IReadOnlyList<ApplicationCatalogEntry> Entries,
    IReadOnlyList<ApplicationCatalogProblem> Problems);

/// <summary>Disposable startup cache; the descriptor directories remain the catalog source of truth.</summary>
public sealed record ApplicationCatalogCache(int SchemaVersion, DateTimeOffset GeneratedAtUtc, IReadOnlyList<ApplicationCatalogCacheEntry> Entries);
public sealed record ApplicationCatalogCacheEntry(string AppId, string Kind, string Source);
