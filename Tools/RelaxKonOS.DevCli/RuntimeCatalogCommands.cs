using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// Authors the publisher side of a managed-runtime catalog. The Helper already verifies signed
/// envelopes and keeps replay history, but nothing produced those envelopes; signing needs the
/// publisher's private key, which must never be reachable from a Server or Helper process. This
/// command is the only place that key is used, and it only ever writes a signed file.
/// </summary>
internal static class RuntimeCatalogCommands
{
    private const int SchemaVersion = 1;
    private const long MaximumArtifactBytes = 2L * 1024 * 1024 * 1024;

    internal static async Task<int> RunAsync(List<string> arguments)
    {
        if (arguments.Count == 0) { Usage(); return 64; }
        var command = arguments[0].ToLowerInvariant();
        arguments.RemoveAt(0);
        try
        {
            switch (command)
            {
                case "keygen": return await KeygenAsync(arguments);
                case "publish": return await PublishAsync(arguments);
                case "verify": return await VerifyAsync(arguments);
                case "source": return await SourceAsync(arguments);
                default: Usage(); return 64;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or FileNotFoundException
            or CryptographicException or RuntimePolicyException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> KeygenAsync(List<string> arguments)
    {
        var keyPath = Require(arguments, "--key");
        var keyId = Require(arguments, "--key-id");
        if (!RuntimeValidation.Identifier(keyId))
            throw new ArgumentException("--key-id must match the runtime identifier grammar.");
        if (File.Exists(keyPath)) throw new ArgumentException("The key file already exists; refusing to replace a signing key.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine("keyId=" + keyId);
        Console.WriteLine("publicKeySpki=" + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static async Task<int> PublishAsync(List<string> arguments)
    {
        var runtimeId = Require(arguments, "--runtime");
        var componentId = Require(arguments, "--component");
        var version = Require(arguments, "--version");
        var rid = Require(arguments, "--rid");
        var sourceId = Require(arguments, "--source-id");
        var keyId = Require(arguments, "--key-id");
        var keyPath = Require(arguments, "--key");
        var archivePath = Require(arguments, "--archive");
        var downloadUri = Require(arguments, "--download-uri");
        var revision = long.Parse(Option(arguments, "--revision") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var issued = DateTimeOffset.TryParse(Option(arguments, "--issued"), out var issuedAt) ? issuedAt : DateTimeOffset.UtcNow;
        var expires = DateTimeOffset.TryParse(Option(arguments, "--expires"), out var expiresAt) ? expiresAt : issued.AddDays(7);
        var channel = string.Equals(Option(arguments, "--channel"), "prerelease", StringComparison.OrdinalIgnoreCase)
            ? RuntimeChannel.Prerelease : RuntimeChannel.Stable;
        var os = Option(arguments, "--os") ?? "linux";
        var architecture = Option(arguments, "--architecture") ?? RidArchitecture(rid);
        var serviceHost = Option(arguments, "--service-host") ?? "systemd";
        var entryPointName = Option(arguments, "--entry-point-name") ?? DefaultBinaryName(runtimeId);
        var artifactId = Option(arguments, "--artifact-id") ?? (runtimeId + "-" + rid + "-" + version);
        if (!RuntimeValidation.Identifier(sourceId) || !RuntimeValidation.Identifier(runtimeId)
            || !RuntimeValidation.Identifier(artifactId) || !RuntimeValidation.Identifier(entryPointName))
            throw new ArgumentException("source, runtime, artifact and entry point names must use the identifier grammar.");
        if (revision < 1) throw new ArgumentException("--revision must be at least 1.");
        if (expires <= issued) throw new ArgumentException("--expires must be later than --issued.");
        if (!RuntimeValidation.Https(downloadUri, out var download))
            throw new ArgumentException("--download-uri must be an absolute https URL without a query or fragment.");
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Archive was not found.", archivePath);

        var format = FormatOf(archivePath);
        var layout = await MeasureAsync(archivePath, format, entryPointName);
        var entryPoint = layout.Files.FirstOrDefault(file => file.Executable)?.Path ?? layout.Files[0].Path;
        if (layout.Files.Count == 1) entryPoint = layout.Files[0].Path;
        var archiveDigest = await Sha256Async(archivePath);
        var artifact = new RuntimeArtifact(artifactId, archiveDigest, MaximumArtifactBytes, format, entryPoint, keyId, layout);
        if (!RuntimeArchiveRules.Valid(artifact))
            throw new ArgumentException("The measured archive does not satisfy the runtime archive layout rules.");
        var release = new ManifestRelease(artifactId, runtimeId, version, channel,
            new RuntimeTarget(os, architecture, null, null, RuntimeInstallMode.Archive, serviceHost), artifact,
            download.OriginalString, null);
        var manifest = new RuntimeManifest(SchemaVersion, sourceId, revision, issued, expires, [release], []);
        var payload = RuntimeJson.Serialize(manifest);

        using var key = ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(keyPath));
        var signed = new byte[RuntimeCatalogVerifier.SignatureContext.Length + payload.Length];
        RuntimeCatalogVerifier.SignatureContext.CopyTo(signed);
        payload.CopyTo(signed, RuntimeCatalogVerifier.SignatureContext.Length);
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = RuntimeJson.Serialize(new SignedRuntimeCatalog(SchemaVersion, keyId,
            Convert.ToBase64String(payload), Convert.ToBase64String(signature)));
        var output = Option(arguments, "--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllBytesAsync(output, envelope);
        Console.WriteLine("catalog=" + Path.GetFullPath(output));
        Console.WriteLine("releaseId=" + artifactId);
        Console.WriteLine("artifactSha256=" + archiveDigest);
        Console.WriteLine("entryPoint=" + entryPoint);
        Console.WriteLine("files=" + layout.Files.Count + " expanded=" + layout.Files.Sum(file => file.Length));
        foreach (var file in layout.Files)
            Console.WriteLine($"  {file.Path} {file.Length} {file.Sha256}{(file.Executable ? " x" : "")}");
        _ = componentId;
        return 0;
    }

    private static async Task<int> VerifyAsync(List<string> arguments)
    {
        var catalogPath = Require(arguments, "--catalog");
        var keyId = Require(arguments, "--key-id");
        var publicKey = Require(arguments, "--public-key");
        var sourceId = Require(arguments, "--source-id");
        var runtimeId = Require(arguments, "--runtime");
        var prefix = Require(arguments, "--download-prefix");
        var trustRevision = long.Parse(Option(arguments, "--trust-revision") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTimeOffset.TryParse(Option(arguments, "--now"), out var instant) ? instant : DateTimeOffset.UtcNow;
        var source = new RuntimeSource(sourceId, runtimeId, "RelaxKonOS", RuntimeSourceKind.HttpsManifest,
            prefix, "catalog.json", keyId, publicKey, true, trustRevision,
            [RuntimeChannel.Stable, RuntimeChannel.Prerelease], 4 * 1024 * 1024, 31 * 24 * 60 * 60,
            [prefix], [], []);
        source.Validate();
        var verified = RuntimeCatalogVerifier.Verify(source, await File.ReadAllBytesAsync(catalogPath), CatalogHistory.Empty, now,
            _ => new RuntimeConfigurationSupport("0.0.0", "", false));
        Console.WriteLine("source=" + verified.Catalog.SourceId + " revision=" + verified.Catalog.Revision
            + " expires=" + verified.Catalog.ExpiresAt.ToString("O"));
        foreach (var release in verified.Catalog.Releases)
            Console.WriteLine($"  {release.ReleaseId} {release.SortVersion} sha256={release.Artifact.Sha256} trust={release.Trust}");
        return 0;
    }

    /// <summary>
    /// Emits the host-owned policy an administrator confirms once with the Helper's own
    /// <c>runtime-source confirm</c> command. It carries only public key material: the private key
    /// never leaves the publisher.
    /// </summary>
    private static async Task<int> SourceAsync(List<string> arguments)
    {
        var sourceId = Require(arguments, "--source-id");
        var runtimeId = Require(arguments, "--runtime");
        var baseUri = Require(arguments, "--base-uri");
        var keyId = Require(arguments, "--key-id");
        var publicKey = Require(arguments, "--public-key");
        var publisher = Option(arguments, "--publisher") ?? "RelaxKonOS";
        var manifestPath = Option(arguments, "--manifest-path") ?? "catalog.json";
        var downloadPrefix = Option(arguments, "--download-prefix") ?? baseUri;
        var trustRevision = long.Parse(Option(arguments, "--trust-revision") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (!baseUri.EndsWith('/') || !downloadPrefix.EndsWith('/'))
            throw new ArgumentException("--base-uri and --download-prefix must end with '/'.");
        var source = new RuntimeSource(sourceId, runtimeId, publisher, RuntimeSourceKind.HttpsManifest, baseUri,
            manifestPath, keyId, publicKey, true, trustRevision, [RuntimeChannel.Stable],
            4 * 1024 * 1024, 31 * 24 * 60 * 60, [downloadPrefix], [], []);
        source.Validate();
        var output = Option(arguments, "--out") ?? Path.Combine(Directory.GetCurrentDirectory(), sourceId + ".source.json");
        await File.WriteAllBytesAsync(output, RuntimeJson.Serialize(source));
        Console.WriteLine("source=" + Path.GetFullPath(output));
        Console.WriteLine("confirmationFingerprint=" + source.ConfirmationFingerprint);
        Console.WriteLine("trustRevision=" + source.TrustRevision);
        Console.WriteLine("Confirm once as root: RelaxKonOS.PrivilegedHelper runtime-source confirm "
            + Path.GetFileName(output) + " " + source.TrustRevision + " " + source.ConfirmationFingerprint);
        return 0;
    }

    private static async Task<RuntimeArchiveLayout> MeasureAsync(string archivePath, string format, string entryPointName)    {
        var files = new List<RuntimeArchiveFile>();
        switch (format)
        {
            case "gz":
            {
                await using var compressed = File.OpenRead(archivePath);
                await using var raw = new GZipStream(compressed, CompressionMode.Decompress);
                using var buffer = new MemoryStream();
                await raw.CopyToAsync(buffer);
                var bytes = buffer.ToArray();
                files.Add(new(entryPointName, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), true));
                break;
            }
            case "zip":
            {
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                    var path = Normalize(entry.FullName);
                    await using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    var bytes = buffer.ToArray();
                    files.Add(new(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        IsExecutableName(path)));
                }
                break;
            }
            case "tar.gz":
            {
                await using var compressed = File.OpenRead(archivePath);
                await using var raw = new GZipStream(compressed, CompressionMode.Decompress);
                await using var tar = new TarReader(raw);
                while (await tar.GetNextEntryAsync() is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                    if (entry.DataStream is null) continue;
                    var path = Normalize(entry.Name);
                    using var buffer = new MemoryStream();
                    await entry.DataStream.CopyToAsync(buffer);
                    var bytes = buffer.ToArray();
                    var executable = (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                    files.Add(new(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), executable));
                }
                break;
            }
            default: throw new ArgumentException("Only .gz, .zip and .tar.gz archives are supported.");
        }
        if (files.Count == 0) throw new ArgumentException("The archive declares no files.");
        foreach (var file in files)
            if (!RuntimeArchiveRules.SafePath(file.Path))
                throw new ArgumentException($"Archive entry '{file.Path}' is not a safe relative path.");
        var expanded = files.Sum(file => file.Length);
        // The layout budget counts every entry an extractor may see, directories included.
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            for (var slash = file.Path.IndexOf('/'); slash >= 0; slash = file.Path.IndexOf('/', slash + 1))
                directories.Add(file.Path[..slash]);
        return new(Math.Max(expanded, 1), files.Count + directories.Count + 16, files);
    }

    // A single-member gzip carries no directory tree, so its only member becomes the entry point.
    private static string DefaultBinaryName(string runtimeId) => runtimeId;
    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('.', '/');
    private static bool IsExecutableName(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || !path.Contains('.', StringComparison.Ordinal);
    private static string FormatOf(string path) => path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? "tar.gz"
        : path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip"
        : path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? "gz"
        : throw new ArgumentException("Only .gz, .zip and .tar.gz archives are supported.");
    private static string RidArchitecture(string rid) => rid.EndsWith("-arm64", StringComparison.Ordinal) ? "arm64" : "amd64";

    private static async Task<string> Sha256Async(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
    }

    private static string Require(List<string> arguments, string option) =>
        Option(arguments, option) ?? throw new ArgumentException(option + " is required.");

    private static string? Option(List<string> arguments, string option)
    {
        var index = arguments.FindIndex(value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index == arguments.Count - 1) throw new ArgumentException(option + " requires a value.");
        var value = arguments[index + 1];
        arguments.RemoveAt(index + 1);
        arguments.RemoveAt(index);
        return value;
    }

    private static void Usage() => Console.Error.WriteLine("""
Usage: relaxkonos-dev runtime-catalog <command>

Commands:
  keygen  --key <key.pem> --key-id <id>
  publish --runtime <id> --component <componentId> --version <label> --rid <linux-x64|win-x64|...>
          --source-id <id> --revision <n> --key <key.pem> --key-id <id>
          --archive <archive file> --download-uri <https url> [--expires <iso>] [--issued <iso>]
          [--os <linux|windows>] [--architecture <amd64|arm64>] [--service-host <systemd|scm>]
          [--entry-point-name <binary>] [--artifact-id <id>] [--channel <stable|prerelease>] [--out <catalog.json>]
  verify  --catalog <catalog.json> --key-id <id> --public-key <spki base64> --source-id <id>
          --runtime <id> --download-prefix <https url/> [--trust-revision <n>] [--now <iso>]
  source  --source-id <id> --runtime <id> --base-uri <https url/> --key-id <id> --public-key <spki base64>
          [--manifest-path <catalog.json>] [--download-prefix <https url/>] [--trust-revision <n>] [--out <policy.json>]

publish measures the archive itself: the declared per-file digests come from the bytes this command
read, never from a vendor index. The private key is only ever read here.
""");
}
