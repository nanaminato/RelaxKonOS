using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

internal sealed record AcceptedModulePackageCandidate(ModulePackageManifest Manifest, string ManifestSha256,
    string ArchiveSha256, long ArchiveBytes, string SourceId, string ReleaseId);

/// <summary>
/// Verifies an already accepted signed release and installs it into the immutable module store.
/// The enabled set is intentionally not changed here: the existing module-management plan owns
/// desired state, and installation alone must never report a module as effective.
/// </summary>
internal static class InstalledModuleInstaller
{
    internal static async Task<InstalledModuleRecord> InstallAsync(string contentRoot, IConfiguration configuration,
        IHostEnvironment environment, string sourceId, string archivePath, string releaseId,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await InstallCoreAsync(contentRoot, configuration, environment, sourceId, archivePath, releaseId, now,
            takeHostLock: true, cancellationToken);

    /// <summary>Online install from an already accepted catalog. The running Host only reads
    /// installed.json at startup, so a new immutable version directory can be staged while it runs.</summary>
    internal static async Task<InstalledModuleRecord> InstallFromAcceptedCatalogAsync(string contentRoot,
        IConfiguration configuration, IHostEnvironment environment, string sourceId, string archivePath,
        string releaseId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await InstallCoreAsync(contentRoot, configuration, environment, sourceId, archivePath, releaseId, now,
            takeHostLock: false, cancellationToken);

    private static async Task<InstalledModuleRecord> InstallCoreAsync(string contentRoot, IConfiguration configuration,
        IHostEnvironment environment, string sourceId, string archivePath, string releaseId, DateTimeOffset now,
        bool takeHostLock, CancellationToken cancellationToken)
    {
        var candidate = await InspectAsync(contentRoot, configuration, environment, sourceId, archivePath, releaseId,
            now, takeHostLock, cancellationToken);
        await using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await InstallVerifiedAsync(contentRoot, sourceId, candidate.Manifest, candidate.ManifestSha256,
            candidate.ArchiveSha256, archive, now, cancellationToken);
    }

    internal static async Task<AcceptedModulePackageCandidate> InspectAsync(string contentRoot, IConfiguration configuration,
        IHostEnvironment environment, string sourceId, string archivePath, string releaseId, DateTimeOffset now,
        bool takeHostLock, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(contentRoot) || !Directory.Exists(contentRoot)
            || !Path.IsPathFullyQualified(archivePath) || !File.Exists(archivePath))
            throw new ModulePackageException("module-package-install-request-invalid");
        var serviceId = ModuleManagementStore.ReadRecoveryReport(contentRoot).ServiceId;
        var policy = ModulePackageHostConfiguration.Source(configuration, sourceId);
        var context = ModulePackageHostConfiguration.Context(serviceId, configuration, environment);
        using var catalogStore = takeHostLock ? new ModulePackageCatalogStore(contentRoot, serviceId) : null;
        var document = ModulePackageCatalogStore.Read(contentRoot, serviceId);
        var verified = ModulePackageCatalogStore.ReadCurrentCatalog(document, policy, context, now);
        var release = verified.Catalog.Releases.SingleOrDefault(item => item.Manifest.ReleaseId == releaseId)
            ?? throw new ModulePackageException("module-package-release-not-found");
        ModulePackageCatalogStore.EnsureNotRevoked(document, releaseId, release.ArchiveSha256);
        var envelope = document.Sources[sourceId].Envelope;
        await using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var candidate = await ModulePackageVerifier.VerifyCandidateAsync(archive, Encoding.UTF8.GetBytes(envelope),
            policy, context, verified.NextHistory, releaseId, now, cancellationToken);
        if (ServerModules.Create(new ConfigurationBuilder().Build()).KnownModuleIds.Contains(candidate.Manifest.ModuleId))
            throw new ModulePackageException("module-package-module-id-conflict");
        ValidateDependencies(contentRoot, configuration, candidate.Manifest);
        return new(candidate.Manifest, release.ManifestSha256, candidate.ArchiveSha256, candidate.ArchiveBytes,
            sourceId, releaseId);
    }

    internal static void ValidateDependencies(string contentRoot, IConfiguration configuration, ModulePackageManifest manifest)
    {
        var installed = InstalledModuleStore.Read(contentRoot).ToDictionary(module => module.ModuleId, StringComparer.Ordinal);
        var known = ServerModules.Create(configuration, InstalledModuleCatalog.Load(contentRoot)).KnownModuleIds;
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency.ModuleId == manifest.ModuleId)
                throw new ModulePackageException("module-package-dependency-self");
            if (!known.Contains(dependency.ModuleId))
                throw new ModulePackageException("module-package-dependency-missing");
            if (!installed.TryGetValue(dependency.ModuleId, out var installedDependency)) continue;
            if (installedDependency.Quarantined)
                throw new ModulePackageException("module-package-dependency-quarantined");
            var version = System.Version.Parse(installedDependency.Version);
            if (version < System.Version.Parse(dependency.MinimumVersion)
                || version >= System.Version.Parse(dependency.MaximumVersionExclusive))
                throw new ModulePackageException("module-package-dependency-version");
        }
        var graph = installed.ToDictionary(pair => pair.Key,
            pair => pair.Value.Requires.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        graph[manifest.ModuleId] = manifest.Dependencies.Select(dependency => dependency.ModuleId)
            .ToHashSet(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string moduleId)
        {
            if (!visiting.Add(moduleId)) return moduleId == manifest.ModuleId;
            if (graph.TryGetValue(moduleId, out var dependencies))
                foreach (var dependency in dependencies)
                    if (Visit(dependency)) return true;
            visiting.Remove(moduleId);
            return false;
        }
        if (Visit(manifest.ModuleId)) throw new ModulePackageException("module-package-dependency-cycle");
    }

    internal static async Task<InstalledModuleRecord> InstallVerifiedAsync(string contentRoot, string sourceId,
        ModulePackageManifest manifest, string manifestSha256, string artifactSha256, Stream archive,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var management = Path.GetFullPath(Path.Combine(contentRoot, "data", "module-management"));
        var packagePath = "packages/" + manifest.ModuleId + "/" + manifest.Version;
        var destination = Path.GetFullPath(Path.Combine(management, packagePath));
        if (!destination.StartsWith(management + Path.DirectorySeparatorChar, PathComparison))
            throw new ModulePackageException("module-package-install-path-invalid");
        var current = InstalledModuleStore.Read(contentRoot).SingleOrDefault(module => module.ModuleId == manifest.ModuleId);
        if (Directory.Exists(destination))
        {
            if (current is not null && current.Version == manifest.Version && current.ArtifactSha256 == artifactSha256)
                return current;
            throw new ModulePackageException("module-package-version-conflict");
        }

        var staging = Path.Combine(management, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            archive.Position = 0;
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
            {
                var files = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.FullName == "manifest.json")
                    {
                        var bytes = await ReadBoundedAsync(entry, 4 * 1024 * 1024, cancellationToken);
                        if (Sha256(bytes) != manifestSha256) throw new ModulePackageException("module-package-manifest-digest");
                        await WriteFileAsync(Path.Combine(staging, "manifest.json"), bytes, cancellationToken);
                        continue;
                    }
                    if (!files.TryGetValue(entry.FullName, out var expected) || !seen.Add(entry.FullName))
                        throw new ModulePackageException("module-package-archive-file-set");
                    var path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!path.StartsWith(staging + Path.DirectorySeparatorChar, PathComparison))
                        throw new ModulePackageException("module-package-archive-path-invalid");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        65536, FileOptions.Asynchronous);
                    await using var input = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long length = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        length += read;
                        if (length > expected.Length) throw new ModulePackageException("module-package-archive-expanded-size");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                    if (length != expected.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != expected.Sha256)
                        throw new ModulePackageException("module-package-file-digest");
                }
                if (seen.Count != files.Count) throw new ModulePackageException("module-package-archive-file-set");
            }
            var record = new InstalledModuleRecord(manifest.ModuleId, manifest.Version, manifest.ReleaseId, sourceId,
                manifest.PublisherId, artifactSha256, manifestSha256, packagePath, manifest.EntryAssembly, manifest.EntryType,
                manifest.Capabilities, manifest.Dependencies.Select(dependency => dependency.ModuleId).Distinct(StringComparer.Ordinal).ToArray(),
                manifest.Runtime, now);
            InstalledModuleStore.WriteInstallMetadata(staging, record);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
            staging = string.Empty;
            var modules = InstalledModuleStore.Read(contentRoot).Where(module => module.ModuleId != manifest.ModuleId).ToList();
            modules.Add(record);
            InstalledModuleStore.Write(contentRoot, modules);
            return record;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(staging) && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal static Task<InstalledModuleRecord> RollbackAsync(string contentRoot, string moduleId, string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modules = InstalledModuleStore.Read(contentRoot).ToList();
        var index = modules.FindIndex(module => module.ModuleId == moduleId);
        if (index < 0) throw new ModulePackageException("module-package-installed-module-not-found");
        var target = InstalledModuleStore.ReadInstallMetadata(contentRoot, "packages/" + moduleId + "/" + version);
        if (target.ModuleId != moduleId || target.Version != version || target.InstalledAt == default)
            throw new ModulePackageException("module-package-install-metadata-invalid");
        InstalledModuleStore.VerifyPackage(contentRoot, target);
        modules[index] = target;
        InstalledModuleStore.Write(contentRoot, modules);
        return Task.FromResult(target);
    }

    private static async Task<byte[]> ReadBoundedAsync(ZipArchiveEntry entry, int limit, CancellationToken cancellationToken)
    {
        if (entry.Length is <= 0 or > int.MaxValue || entry.Length > limit) throw new ModulePackageException("module-package-manifest-size");
        var bytes = new byte[(int)entry.Length];
        await using var stream = entry.Open();
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1) throw new ModulePackageException("module-package-manifest-size");
        return bytes;
    }

    private static async Task WriteFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
