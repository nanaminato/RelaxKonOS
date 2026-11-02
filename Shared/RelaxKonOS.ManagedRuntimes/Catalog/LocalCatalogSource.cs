using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RelaxKonOS.ManagedRuntimes.Catalog;

public interface IComponentCatalogSource
{
    string SourceId { get; }
    ResolvedComponentCatalog Load();
    string ManifestReference(string componentId);
    Task<Stream> OpenAsync(string reference, string declaringFile, CancellationToken ct = default);
}

/// <summary>
/// Reads a deployment-owned, immutable local directory. No network, ambient directory scanning,
/// provider code loading, trust registration or Helper paths can originate in these files.
/// </summary>
public sealed class LocalCatalogSource(string root, string sourceId) : IComponentCatalogSource
{
    private readonly string _root = Path.GetFullPath(root);
    public string SourceId { get; } = sourceId;
    private const int MaximumBytes = 256 * 1024;
    public ResolvedComponentCatalog Load() => ComponentCatalogFormat.Load(ReadBytes,
        (reference, declaringFile) => ResolveReference(reference, declaringFile));

    public string ManifestReference(string componentId) => Read<ComponentCatalogIndex>("catalog.json", "catalog.json")
        .Components.Single(entry => entry.Id == componentId).Manifest;

    public Task<Stream> OpenAsync(string reference, string declaringFile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(ResolveReference(reference, declaringFile), FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    /// <summary>References are relative to the declaring file, and always confined to this root.</summary>
    public string ResolveReference(string reference, string declaringFile = "catalog.json")
    {
        static bool Relative(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value)
            && !value.Contains('\\') && !value.Contains(':') && !value.Contains('%') && !value.Contains('?')
            && !value.Contains('#') && !value.Any(char.IsControl)
            && value.Split('/').All(segment => segment.Length > 0 && !segment.EndsWith(' ') && (segment == "." || segment == ".." || !segment.EndsWith('.')));
        if (!Relative(reference) || !Relative(declaringFile)) throw Invalid();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        var declaration = Path.GetFullPath(Path.Combine(_root, declaringFile));
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(declaration)!, reference));
        if (!declaration.StartsWith(prefix, comparison) || !path.StartsWith(prefix, comparison)) throw Invalid();
        // Check every existing ancestor, including ancestors above the selected root. Never follow
        // symlinks/junctions, even when a particular link currently points back inside the root.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        return path;
    }

    private byte[] ReadBytes(string reference, string declaringFile)
    {
        var path = ResolveReference(reference, declaringFile);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw Invalid();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (memory.Length + count > MaximumBytes) throw Invalid();
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    private T Read<T>(string reference, string declaringFile) => ComponentCatalogFormat.Read<T>(ReadBytes(reference, declaringFile));
    private static InvalidDataException Invalid() => new("component.catalog_invalid");
}

public static class BundledComponentCatalog
{
    public static LocalCatalogSource Source { get; } = new(Path.Combine(AppContext.BaseDirectory, "ComponentCatalog"), "relaxkonos-bundled");
    private static readonly Lazy<ResolvedComponentCatalog> Catalog = new(() =>
        Source.Load());
    public static ResolvedComponentCatalog Current => Catalog.Value;
}
