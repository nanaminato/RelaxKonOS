using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;

/// <summary>One root the host administrator approved for module file or data work. A module names the
/// root in its manifest, but only this root-owned document can turn that declaration into an adapter.</summary>
public sealed record ModuleResourceRoot(string Kind, string Root, string[] Actions, long MaximumBytes,
    int Mode = 420, uint Owner = 0, uint Group = 0);

/// <summary>
/// Root-owned allowlist of module file/data roots. It is read on every request, never cached, and a
/// missing, non-root-owned, linked or group/other-writable file means no root is approved.
/// </summary>
public sealed record ModuleResourcePolicy(int SchemaVersion, ModuleResourceRoot[] Roots)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
    };

    public static bool TryLoad(string path, out ModuleResourcePolicy? policy, out string problem)
    {
        policy = null;
        problem = "module-resource-policy-unavailable";
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 65536 || info.LinkTarget is not null) return false;
            var (owner, _) = LinuxFileOwnership.Read(path);
            if (owner != 0) return false;
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
            return TryParse(File.ReadAllBytes(path), out policy, out problem);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Parse the exact documented camelCase shape with no unknown members and schemaVersion=1.</summary>
    public static bool TryParse(ReadOnlySpan<byte> json, out ModuleResourcePolicy? policy, out string problem)
    {
        policy = null;
        problem = "module-resource-policy-invalid";
        try
        {
            var parsed = JsonSerializer.Deserialize<ModuleResourcePolicy>(json, Options);
            if (parsed is null || parsed.SchemaVersion != 1 || !parsed.Valid()) return false;
            policy = parsed;
            problem = "";
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return false;
        }
    }

    public bool TryFindFile(string? root, string? action, out ModuleResourceRoot? entry) =>
        TryFind("file", root, action, ModuleCapabilityCatalog.FileManage, out entry);

    public bool TryFindData(string? root, string? action, out ModuleResourceRoot? entry) =>
        TryFind("data", root, action, ModuleCapabilityCatalog.DataManage, out entry);

    private bool TryFind(string kind, string? root, string? action, string category, out ModuleResourceRoot? entry)
    {
        entry = Roots.FirstOrDefault(item => item.Kind == kind && item.Root == root
            && item.Actions.Contains(action!, StringComparer.Ordinal));
        return entry is not null && ModuleCapabilityCatalog.IsAction(category, action);
    }

    private bool Valid()
    {
        if (Roots is not { Length: > 0 and <= 64 }) return false;
        if (Roots.Any(root => root is null)) return false;
        if (Roots.Select(root => root!.Kind + ":" + root.Root).Distinct(StringComparer.Ordinal).Count() != Roots.Length)
            return false;
        foreach (var root in Roots)
        {
            if (root!.Kind is not ("file" or "data") || !ModuleResourceIdentity.CanonicalPath(root.Root)) return false;
            if (root.Actions is not { Length: > 0 and <= 16 }
                || root.Actions.Distinct(StringComparer.Ordinal).Count() != root.Actions.Length) return false;
            var category = root.Kind == "file" ? ModuleCapabilityCatalog.FileManage : ModuleCapabilityCatalog.DataManage;
            if (!root.Actions.All(action => ModuleCapabilityCatalog.IsAction(category, action))) return false;
            // The policy may only approve actions this Helper actually executes. Declarable-but-not-delivered
            // actions must fail at policy load, not become a confusing runtime refusal.
            var delivered = root.Kind == "file" ? new[] { "write", "delete" } : new[] { "preserve", "backup", "delete" };
            if (!root.Actions.All(action => delivered.Contains(action, StringComparer.Ordinal))) return false;
            if (root.MaximumBytes is <= 0 or > 1073741824) return false;
            if (root.Kind == "file" && (root.Mode is < 0 or > 0x1ff || root.Owner > 1000000 || root.Group > 1000000))
                return false;
        }
        return true;
    }
}
