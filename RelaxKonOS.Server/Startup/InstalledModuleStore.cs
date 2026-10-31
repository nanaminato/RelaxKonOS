using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.ServerModule.Abstractions;

namespace RelaxKonOS.Server.Startup;

/// <summary>Installation fact for one independently installed module version. The enabled set and
/// operation receipts remain in ModuleManagementStore; this document only says what is on disk.</summary>
internal sealed record InstalledModuleRecord(
    string ModuleId,
    string Version,
    string ReleaseId,
    string SourceId,
    string PublisherId,
    string ArtifactSha256,
    string ManifestSha256,
    string PackagePath,
    string EntryAssembly,
    string EntryType,
    string[] Capabilities,
    string[] Requires,
    DateTimeOffset InstalledAt,
    bool Quarantined = false);

internal sealed record InstalledModuleDocument(int SchemaVersion, InstalledModuleRecord[] Modules);

/// <summary>Reads and atomically replaces <c>data/module-management/installed.json</c>.</summary>
internal static class InstalledModuleStore
{
    private const int Limit = 4 * 1024 * 1024;
    private const int MaximumModules = 1024;
    internal static string PathFor(string contentRoot) =>
        Path.Combine(contentRoot, "data", "module-management", "installed.json");

    internal static IReadOnlyList<InstalledModuleRecord> Read(string contentRoot)
    {
        var path = PathFor(contentRoot);
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > Limit) throw Invalid();
        InstalledModuleDocument? document;
        try { document = JsonSerializer.Deserialize<InstalledModuleDocument>(File.ReadAllBytes(path), RelaxKonOSJsonOptions.Default); }
        catch (JsonException) { throw Invalid(); }
        if (document is null || document.SchemaVersion != 1 || document.Modules is not { Length: <= MaximumModules })
            throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in document.Modules)
        {
            if (module is null || !Valid(module) || !ids.Add(module.ModuleId)) throw Invalid();
        }
        return document.Modules;
    }

    /// <summary>Reads the per-version installation metadata written beside the immutable package.</summary>
    internal static InstalledModuleRecord ReadInstallMetadata(string contentRoot, string packagePath)
    {
        var root = ResolvePackageRoot(contentRoot, packagePath);
        var path = Path.Combine(root, "install.json");
        if (!File.Exists(path)) throw new ModulePackageException("module-package-install-metadata-missing");
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 64 * 1024) throw new ModulePackageException("module-package-install-metadata-invalid");
        InstalledModuleRecord? record;
        try { record = JsonSerializer.Deserialize<InstalledModuleRecord>(File.ReadAllBytes(path), RelaxKonOSJsonOptions.Default); }
        catch (JsonException) { throw new ModulePackageException("module-package-install-metadata-invalid"); }
        if (record is null || !Valid(record) || record.PackagePath != packagePath)
            throw new ModulePackageException("module-package-install-metadata-invalid");
        return record;
    }

    internal static void WriteInstallMetadata(string packageRoot, InstalledModuleRecord record)
    {
        if (!Valid(record)) throw Invalid();
        File.WriteAllBytes(Path.Combine(packageRoot, "install.json"),
            JsonSerializer.SerializeToUtf8Bytes(record, RelaxKonOSJsonOptions.Default));
    }

    /// <summary>Lists installed versions for one module by reading only their metadata, never an assembly.</summary>
    internal static IReadOnlyList<InstalledModuleRecord> ReadVersions(string contentRoot, string moduleId)
    {
        if (!Identifier(moduleId)) throw new ModulePackageException("module-package-installed-state-invalid");
        var root = Path.Combine(contentRoot, "data", "module-management", "packages", moduleId);
        if (!Directory.Exists(root)) return [];
        var records = new List<InstalledModuleRecord>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var metadata = Path.Combine(directory, "install.json");
            if (!File.Exists(metadata)) continue;
            var record = ReadInstallMetadata(contentRoot, "packages/" + moduleId + "/" + Path.GetFileName(directory));
            if (record.ModuleId == moduleId) records.Add(record);
        }
        return records.OrderBy(record => System.Version.Parse(record.Version)).ToArray();
    }

    internal static void Write(string contentRoot, IReadOnlyList<InstalledModuleRecord> modules)
    {
        if (modules.Count > MaximumModules || modules.Select(m => m.ModuleId).Distinct(StringComparer.Ordinal).Count() != modules.Count)
            throw Invalid();
        var directory = Path.GetDirectoryName(PathFor(contentRoot))!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = PathFor(contentRoot);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new InstalledModuleDocument(1, modules.ToArray()), RelaxKonOSJsonOptions.Default);
            if (bytes.Length > Limit) throw Invalid();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       65536, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(true);
            }
            // The maintenance command normally runs as root while the Server service account reads this
            // document. Preserve the state owner instead of leaving installed.json unreadable to the service.
            if (OperatingSystem.IsLinux() && Environment.UserName == "root")
            {
                var state = Path.Combine(directory, "state.json");
                if (File.Exists(state)) ModuleManagementStore.PreserveUnixOwner(temporary, state);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Remove(string contentRoot, string moduleId, bool deleteFiles)
    {
        var modules = Read(contentRoot).ToList();
        var removed = modules.RemoveAll(module => module.ModuleId == moduleId);
        if (removed == 0) throw new ModulePackageException("module-package-installed-module-not-found");
        if (deleteFiles)
        {
            foreach (var module in Read(contentRoot).Where(module => module.ModuleId == moduleId))
            {
                var path = ResolvePackageRoot(contentRoot, module.PackagePath);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
        Write(contentRoot, modules);
    }

    internal static string ResolvePackageRoot(string contentRoot, string packagePath)
    {
        var management = Path.GetFullPath(Path.Combine(contentRoot, "data", "module-management"));
        var path = Path.GetFullPath(Path.Combine(management, packagePath));
        if (!path.StartsWith(management + Path.DirectorySeparatorChar, PathComparison) || !Directory.Exists(path))
            throw new ModulePackageException("module-package-installed-path-invalid");
        return path;
    }

    /// <summary>Re-hashes the installed files before an enabled module is loaded. Installation state is
    /// not trusted as proof of the bytes currently on disk.</summary>
    internal static void VerifyPackage(string contentRoot, InstalledModuleRecord record)
    {
        var root = ResolvePackageRoot(contentRoot, record.PackagePath);
        var manifestPath = Path.Combine(root, "manifest.json");
        RequireRegularFile(manifestPath);
        var info = new FileInfo(manifestPath);
        if (info.Length is <= 0 or > 4 * 1024 * 1024) throw new ModulePackageException("module-package-installed-integrity");
        ModulePackageManifest manifest;
        try { manifest = ModulePackageJson.Read<ModulePackageManifest>(File.ReadAllBytes(manifestPath)); }
        catch (ModulePackageException) { throw; }
        catch (Exception error) when (error is IOException or JsonException) { throw new ModulePackageException("module-package-installed-integrity"); }
        if (manifest.ModuleId != record.ModuleId || manifest.Version != record.Version
            || manifest.ReleaseId != record.ReleaseId || manifest.PublisherId != record.PublisherId
            || manifest.EntryAssembly != record.EntryAssembly || manifest.EntryType != record.EntryType
            || !manifest.Files.Any(file => file.Path == record.EntryAssembly))
            throw new ModulePackageException("module-package-installed-identity");
        foreach (var file in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)) throw new ModulePackageException("module-package-installed-path-invalid");
            RequireRegularFile(path);
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length != file.Length) throw new ModulePackageException("module-package-installed-integrity");
            using var stream = File.OpenRead(path);
            var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (digest != file.Sha256) throw new ModulePackageException("module-package-installed-integrity");
        }
    }

    private static bool Valid(InstalledModuleRecord module) =>
        Identifier(module.ModuleId) && Version(module.Version) && Identifier(module.ReleaseId)
        && Identifier(module.SourceId) && Identifier(module.PublisherId) && Digest(module.ArtifactSha256)
        && Digest(module.ManifestSha256) && PackagePath(module.PackagePath) && EntryPath(module.EntryAssembly)
        && module.EntryType is { Length: > 0 and <= 256 } && module.Capabilities is { Length: <= 128 }
        && module.Requires is { Length: <= 128 } && module.Capabilities.All(Identifier)
        && module.Requires.All(Identifier) && module.PackagePath == "packages/" + module.ModuleId + "/" + module.Version
        && module.InstalledAt != default;

    private static void RequireRegularFile(string path)
    {
        if (!File.Exists(path)) throw new ModulePackageException("module-package-installed-path-invalid");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ModulePackageException("module-package-installed-path-invalid");
    }

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 96 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') && value is not ("core" or "relaxkonos.core");
    private static bool Version(string? value) => System.Version.TryParse(value, out var parsed)
        && parsed.Build >= 0 && parsed.Revision == -1 && parsed.ToString(3) == value;
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static bool PackagePath(string? value) => value is { Length: > 0 and <= 256 } && value.StartsWith("packages/", StringComparison.Ordinal)
        && value.Count(c => c == '/') == 2 && value.Split('/').All(part => part.Length > 0 && part is not "." and not ".."
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
    private static bool EntryPath(string? value) => value is { Length: > 0 and <= 256 } && value.StartsWith("server/", StringComparison.Ordinal)
        && !value.Contains('\\') && value.Split('/').All(part => part.Length > 0 && part is not "." and not ".."
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) && value.EndsWith(".dll", StringComparison.Ordinal);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static ModulePackageException Invalid() => new("module-package-installed-state-invalid");
}

internal interface IInstalledModuleInfo
{
    string InstalledVersion { get; }
    string ArtifactDigest { get; }
    string SourceId { get; }
    string TrustState { get; }
}

/// <summary>Loads installed module metadata without executing any package code. Assembly loading is
/// deferred until the module is actually enabled and its registration phase runs.</summary>
internal static class InstalledModuleCatalog
{
    internal static IReadOnlyList<IServerModule> Load(string contentRoot) =>
        InstalledModuleStore.Read(contentRoot).Select(record => (IServerModule)new InstalledServerModule(record, contentRoot)).ToArray();
}

/// <summary>Adapts the public installed-module ABI to the internal server-module lifecycle.</summary>
internal sealed class InstalledServerModule : IServerModule, IInstalledModuleInfo
{
    private readonly InstalledModuleRecord _record;
    private readonly string _contentRoot;
    private IInstalledServerModule? _module;
    private InstalledModuleLoadContext? _loadContext;

    internal InstalledServerModule(InstalledModuleRecord record, string contentRoot)
    {
        _record = record;
        _contentRoot = contentRoot;
    }

    public string Id => _record.ModuleId;
    public IReadOnlyList<string> Capabilities => _record.Quarantined ? [] : _record.Capabilities;
    public IReadOnlyList<string> Requires => _record.Requires;
    public string InstalledVersion => _record.Version;
    public string ArtifactDigest => _record.ArtifactSha256;
    public string SourceId => _record.SourceId;
    public string TrustState => _record.Quarantined ? "quarantined" : "installed";

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (_record.Quarantined) return;
        EnsureLoaded().ConfigureServices(builder.Services, CreateContext(context));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        if (_record.Quarantined) return;
        EnsureLoaded().MapEndpoints(endpoints, CreateContext(context));
    }

    private InstalledModuleContext CreateContext(ServerModuleContext context)
    {
        var dataPath = Path.Combine(_contentRoot, "data", "module-data", _record.ModuleId);
        Directory.CreateDirectory(dataPath);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(dataPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new(context.Configuration, context.Environment, _contentRoot, dataPath,
            context.Platform.ToString(), context.StorageProvider);
    }

    private IInstalledServerModule EnsureLoaded()
    {
        if (_module is not null) return _module;
        InstalledModuleStore.VerifyPackage(_contentRoot, _record);
        var packageRoot = InstalledModuleStore.ResolvePackageRoot(_contentRoot, _record.PackagePath);
        var entry = Path.GetFullPath(Path.Combine(packageRoot, _record.EntryAssembly));
        if (!entry.StartsWith(packageRoot + Path.DirectorySeparatorChar, PathComparison) || !File.Exists(entry))
            throw new InvalidOperationException($"Installed module {Id} entry assembly is missing or outside its package root.");
        _loadContext = new InstalledModuleLoadContext(entry);
        var assembly = _loadContext.LoadFromAssemblyPath(entry);
        var type = assembly.GetType(_record.EntryType, throwOnError: false, ignoreCase: false)
            ?? throw new InvalidOperationException($"Installed module {Id} entry type was not found.");
        if (!typeof(IInstalledServerModule).IsAssignableFrom(type) || Activator.CreateInstance(type) is not IInstalledServerModule module)
            throw new InvalidOperationException($"Installed module {Id} does not implement the Host module ABI.");
        return _module = module;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class InstalledModuleLoadContext : AssemblyLoadContext
    {
        private static readonly Assembly Abstractions = typeof(IInstalledServerModule).Assembly;
        private static readonly Assembly Protocol = typeof(ServerCapabilities).Assembly;
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _entryDirectory;

        internal InstalledModuleLoadContext(string entry) : base("installed-module:" + Path.GetFileNameWithoutExtension(entry))
        {
            _resolver = new AssemblyDependencyResolver(entry);
            _entryDirectory = Path.GetDirectoryName(entry)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == Abstractions.GetName().Name) return Abstractions;
            if (assemblyName.Name == Protocol.GetName().Name) return Protocol;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null && assemblyName.Name is { } name)
            {
                var candidate = Path.Combine(_entryDirectory, name + ".dll");
                if (File.Exists(candidate)) path = candidate;
            }
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null)
            {
                var candidate = Path.Combine(_entryDirectory, unmanagedDllName);
                if (File.Exists(candidate)) path = candidate;
            }
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
