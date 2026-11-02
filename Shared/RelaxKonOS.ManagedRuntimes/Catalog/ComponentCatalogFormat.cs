using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RelaxKonOS.ManagedRuntimes.Catalog;

public static class ComponentCatalogFormat
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 24
    };

    public static ResolvedComponentCatalog Load(Func<string, string, byte[]> read, Action<string, string> checkArtifact)
    {
        var index = Read<ComponentCatalogIndex>(read("catalog.json", "catalog.json"));
        if (index.SchemaVersion != 1 || index.Components is null || index.Workloads is null || index.Components.Count > 256)
            throw Invalid();
        var components = new List<ComponentManifest>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in index.Components)
        {
            if (entry is null || !Id(entry.Id) || !ids.Add(entry.Id)) throw Invalid();
            var manifest = Read<ComponentManifest>(read(entry.Manifest, "catalog.json"));
            Validate(manifest);
            if (manifest.Id != entry.Id) throw Invalid();
            foreach (var target in manifest.Targets.Where(target => target.Artifact.Reference is not null))
                checkArtifact(target.Artifact.Reference!, entry.Manifest);
            components.Add(manifest);
        }
        var workloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workload in index.Workloads)
            if (workload is null || !Id(workload.WorkloadId) || !workloads.Add(workload.WorkloadId)
                || string.IsNullOrWhiteSpace(workload.DisplayNameKey) || !Ids(workload.RequiredComponents) || !Ids(workload.OptionalComponents)
                || workload.RequiredComponents.Concat(workload.OptionalComponents).Any(id => !ids.Contains(id))) throw Invalid();
        foreach (var component in components)
            if (component.RequiredComponents.Concat(component.OptionalComponents).Concat(component.Conflicts).Any(id => !ids.Contains(id))) throw Invalid();
        return new(components.AsReadOnly(), index.Workloads);
    }

    public static T Read<T>(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > 262144) throw Invalid();
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 24 });
        RejectDuplicates(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw Invalid();
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }

    private static bool Id(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9.-]{0,127}\\z");
    private static bool Ids(IReadOnlyList<string>? values) => values is not null && values.All(Id) && values.Distinct().Count() == values.Count;
    private static void Validate(ComponentManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || !Id(manifest.Id) || !Id(manifest.RuntimeId) || !Id(manifest.Category)
            || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.DescriptionKey)
            || manifest.Integration is not ("Batch" or "Runtime" or "Application" or "Installation")
            || manifest.ApplicationId is not null && !Id(manifest.ApplicationId)
            || !Ids(manifest.RequiredComponents) || !Ids(manifest.OptionalComponents) || !Ids(manifest.Conflicts) || !Ids(manifest.Applications)
            || manifest.Targets is null || manifest.Targets.Count is 0 or > 32) throw Invalid();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (manifest.Targets.Any(target => target?.Installer?.Type == "roapp")
            && manifest.Targets.Any(target => target?.Installer?.Type != "roapp")) throw Invalid();
        foreach (var target in manifest.Targets)
        {
            if (target is null || target.Os is not ("linux" or "windows" or "macos") || !Ids(target.Architectures)
                || target.Architectures.Count == 0 || target.Architectures.Any(arch => arch is not ("x64" or "arm64" or "x86" or "arm"))
                || target.Installer is null || target.Installer.Type is not ("roapp" or "apt" or "download" or "archive")
                || (target.SourceKind, target.Installer.Type) is not (("ManagedArchive", "archive") or ("SystemPackage", "apt")
                    or ("OfficialSource", "download") or ("NativeInstaller", "download") or ("DesktopApp", "roapp"))
                || target.OfficialDownloads && target.Installer.Type != "archive"
                || target.Releases is null || target.Artifact is null
                || !Id(target.Releases.Type) || !Id(target.Artifact.Selector)) throw Invalid();
            ComponentReleaseRules.Validate(target);
            foreach (var arch in target.Architectures)
                if (!keys.Add(target.Os + "|" + arch + "|" + target.SourceKind)) throw Invalid();
        }
    }
    private static InvalidDataException Invalid() => new("component.catalog_invalid");
}
