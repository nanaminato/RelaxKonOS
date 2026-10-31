using System.Text.Json;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Explicit code trust, bound to exact installed bytes; updates never inherit old consent.</summary>
internal static class ModuleTrustStore
{
    internal sealed record Consent(string ModuleId, string Version, string PublisherId, string ArtifactSha256,
        string ManifestSha256, string Actor, string Revision, DateTimeOffset GrantedAt, bool Trusted);
    private sealed record Document(int SchemaVersion, Consent[] Consents);
    private static readonly object Gate = new();
    private static string PathFor(string root) => Path.Combine(root, "data", "module-management", "module-trust.json");
    internal static Consent? Read(string root, InstalledModuleRecord module)
    {
        lock (Gate)
            return ReadAll(root).SingleOrDefault(c => c.ModuleId == module.ModuleId && c.Version == module.Version
                && c.PublisherId == module.PublisherId && c.ArtifactSha256 == module.ArtifactSha256 && c.ManifestSha256 == module.ManifestSha256);
    }
    private static Consent[] ReadAll(string root)
    {
        var path = PathFor(root);
        if (!File.Exists(path)) return [];
        var document = ModulePackageJson.Read<Document>(File.ReadAllBytes(path));
        if (document.SchemaVersion != 2 || document.Consents.Length > 1000
            || document.Consents.Select(c => c.ModuleId).Distinct().Count() != document.Consents.Length)
            throw new ModulePackageException("module-package-trust-invalid");
        return document.Consents;
    }
    internal static Consent Set(string root, InstalledModuleRecord module, string actor, bool trusted)
    {
        lock (Gate)
        {
            var consents = ReadAll(root).Where(c => c.ModuleId != module.ModuleId).ToList();
            var consent = new Consent(module.ModuleId, module.Version, module.PublisherId, module.ArtifactSha256,
                module.ManifestSha256, actor, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, trusted);
            consents.Add(consent);
            var path = PathFor(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    stream.Write(JsonSerializer.SerializeToUtf8Bytes(new Document(2, consents.ToArray()), ModulePackageJson.Options));
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return consent;
        }
    }
}
