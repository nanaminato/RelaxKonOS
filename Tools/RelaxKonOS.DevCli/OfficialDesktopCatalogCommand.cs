using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Developer;

internal static class OfficialDesktopCatalogCommand
{
    internal static async Task RunAsync(string output, IReadOnlyList<string> packages)
    {
        if (packages.Count is 0 or > OfficialDesktopCatalogRules.MaximumReleases)
            throw new ArgumentException("Provide at least one official .roapp and at most 128 releases.");
        output = Path.GetFullPath(output);
        var releases = new List<(string Path, OfficialDesktopCatalogEntry Entry)>();
        var versions = new HashSet<(string, string)>();
        foreach (var path in packages)
        {
            if (!Path.GetExtension(path).Equals(".roapp", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only .roapp packages can be published in the official desktop catalog.");
            await using var file = File.OpenRead(path);
            if (file.Length is <= 0 or > OfficialDesktopCatalogRules.MaximumPackageBytes)
                throw new ArgumentException("The .roapp package exceeds its allowed size.");
            JsonElement body;
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
            {
                var manifest = archive.GetEntry("manifest.json");
                if (manifest is not { Length: > 0 and <= 1024 * 1024 })
                    throw new ArgumentException("The .roapp package has no bounded manifest.");
                using var input = manifest.Open();
                using var document = await JsonDocument.ParseAsync(input);
                body = document.RootElement.Clone();
            }
            file.Position = 0;
            var entry = new OfficialDesktopCatalogEntry(Read("id") ?? "", Read("version") ?? "", Read("displayName") ?? "",
                Read("description"), Convert.ToHexStringLower(await SHA256.HashDataAsync(file)), file.Length);
            if (!OfficialDesktopCatalogRules.IsValid(entry) || !versions.Add((entry.AppId, entry.Version)))
                throw new ArgumentException("Invalid or repeated official app version.");
            releases.Add((Path.GetFullPath(path), entry));

            string? Read(string key) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        foreach (var release in releases)
        {
            var path = Path.Combine(output, OfficialDesktopCatalogRules.ArtifactPath(release.Entry.AppId, release.Entry.Version));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                await RequireReleaseAsync(path, release.Entry);
                continue;
            }
            var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(release.Path, staging);
                await RequireReleaseAsync(staging, release.Entry);
                File.Move(staging, path);
            }
            finally { File.Delete(staging); }
        }

        var catalog = Path.Combine(output, "catalog.json");
        var temporary = catalog + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = new OfficialDesktopCatalogDocument(OfficialDesktopCatalogRules.SchemaVersion,
                releases.Select(release => release.Entry).OrderBy(entry => entry.AppId, StringComparer.Ordinal)
                    .ThenByDescending(entry => Version.Parse(entry.Version)).ToArray());
            var json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            if (json.Length > OfficialDesktopCatalogRules.MaximumCatalogBytes)
                throw new ArgumentException("The official desktop catalog exceeds its allowed size.");
            await File.WriteAllBytesAsync(temporary, json);
            File.Move(temporary, catalog, overwrite: true);
        }
        finally { File.Delete(temporary); }
        Console.WriteLine(catalog);
    }

    private static async Task RequireReleaseAsync(string path, OfficialDesktopCatalogEntry release)
    {
        await using var file = File.OpenRead(path);
        if (file.Length != release.Bytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(file)) != release.Sha256)
            throw new InvalidOperationException("An official app version has different package bytes. Publish a new version.");
    }
}
