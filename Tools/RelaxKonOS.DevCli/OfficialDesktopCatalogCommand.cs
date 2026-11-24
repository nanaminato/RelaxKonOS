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

        var documentToPublish = new OfficialDesktopCatalogDocument(OfficialDesktopCatalogRules.SchemaVersion,
            releases.Select(release => release.Entry).OrderBy(entry => entry.AppId, StringComparer.Ordinal)
                .ThenByDescending(entry => Version.Parse(entry.Version)).ToArray());
        var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(documentToPublish,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        if (catalogBytes.Length > OfficialDesktopCatalogRules.MaximumCatalogBytes)
            throw new ArgumentException("The official desktop catalog exceeds its allowed size.");

        var pending = new List<(string Source, string Destination, OfficialDesktopCatalogEntry Entry)>();
        foreach (var release in releases)
        {
            var path = Path.Combine(output, OfficialDesktopCatalogRules.ArtifactPath(release.Entry.AppId, release.Entry.Version));
            if (File.Exists(path))
                await RequireReleaseAsync(path, release.Entry);
            else
                pending.Add((release.Path, path, release.Entry));
        }

        foreach (var release in pending)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(release.Destination)!);
            var staging = release.Destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(release.Source, staging);
                await RequireReleaseAsync(staging, release.Entry);
                try { File.Move(staging, release.Destination); }
                catch (IOException) when (File.Exists(release.Destination))
                {
                    await RequireReleaseAsync(release.Destination, release.Entry);
                }
            }
            finally { File.Delete(staging); }
        }

        var catalog = Path.Combine(output, "catalog.json");
        var temporary = catalog + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, catalogBytes);
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
