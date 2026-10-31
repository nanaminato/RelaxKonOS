using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Offline acceptance owns the same lifetime lock as the Host. A single atomic document
/// commits signed catalogs, monotonic trust evidence, cross-source identities and receipts together.</summary>
internal sealed class ModulePackageCatalogStore : IDisposable
{
    internal sealed record SavedSource(string Envelope, ModulePackageTrustHistory History);
    internal sealed record Receipt(Guid OperationId, string Actor, string IdempotencyKey, string Fingerprint,
        string ServiceId, string SourceId, string Revision, long Sequence, string PayloadSha256, long PolicyRevision, DateTimeOffset AcceptedAt);
    internal sealed record Document(int SchemaVersion, string ServiceId, string Revision,
        Dictionary<string, SavedSource> Sources, Dictionary<string, string> VersionIdentities, Receipt[] Receipts);
    internal const string InitialRevision = "uninitialized";
    private const int Limit = 32 * 1024 * 1024;
    private readonly string _path;
    private readonly string _marker;
    private readonly string _identityPath;
    private readonly FileStream? _hostLock;
    private readonly FileStream _catalogLock;
    private readonly string _serviceId;
    private string? _fileHash;
    private Document _document;

    internal ModulePackageCatalogStore(string contentRoot, string expectedServiceId, bool takeHostLock = true)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw new ModulePackageException("module-package-platform-unsupported");
        var directory = Path.Combine(contentRoot, "data", "module-management");
        _path = Path.Combine(directory, "package-catalogs.json");
        _marker = Path.Combine(directory, "package-catalogs.initialized");
        _identityPath = Path.Combine(directory, "state.json");
        _serviceId = expectedServiceId;
        FileStream? hostLock = null;
        FileStream? catalogLock = null;
        try
        {
            if (takeHostLock)
            {
                try { hostLock = new FileStream(Path.Combine(directory, "host.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { throw new ModulePackageException("module-package-host-lock-unavailable"); }
            }
            try
            {
                catalogLock = new FileStream(Path.Combine(directory, "package-catalogs.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { throw new ModulePackageException("module-package-catalog-lock-unavailable"); }
            if (ModuleManagementStore.ReadRecoveryReport(contentRoot).ServiceId != expectedServiceId)
                throw new ModulePackageException("module-package-service-mismatch");
            var snapshot = ReadSnapshot(contentRoot, expectedServiceId);
            _document = snapshot.Document;
            _fileHash = snapshot.Hash;
            _hostLock = hostLock;
            _catalogLock = catalogLock;
        }
        catch
        {
            hostLock?.Dispose();
            catalogLock?.Dispose();
            throw;
        }
    }

    internal static Document Read(string contentRoot, string serviceId) => ReadSnapshot(contentRoot, serviceId).Document;
    private static (Document Document, string? Hash) ReadSnapshot(string contentRoot, string serviceId)
    {
        var directory = Path.Combine(contentRoot, "data", "module-management");
        var path = Path.Combine(directory, "package-catalogs.json");
        var marker = Path.Combine(directory, "package-catalogs.initialized");
        var hasState = RegularFile(path);
        var hasMarker = RegularFile(marker);
        if (!hasState && !hasMarker) return (new(1, serviceId, InitialRevision, new(StringComparer.Ordinal), new(StringComparer.Ordinal), []), null);
        if (!hasState || !hasMarker || Encoding.UTF8.GetString(ReadBounded(marker, 256)) != serviceId)
            throw new ModulePackageException("module-package-state-recovery-required");
        var bytes = ReadBounded(path, Limit);
        var document = ModulePackageJson.Read<Document>(bytes, Limit);
        if (document.SchemaVersion != 1 || document.ServiceId != serviceId || !Guid.TryParseExact(document.Revision, "N", out _)
            || document.Sources is not { Count: > 0 and <= 16 } || document.VersionIdentities is not { Count: <= 10000 }
            || document.Receipts is not { Length: > 0 and <= 10000 }
            || document.Sources.Any(p => p.Value?.History is null || p.Value.Envelope is not { Length: > 0 and <= 6291456 }
                || p.Key != p.Value.History.SourceId || p.Value.History.ServiceId != serviceId)
            || document.VersionIdentities.Any(p => p.Key.Length > 256 || !Digest(p.Value))
            || document.Receipts.Any(r => r is null || r.OperationId == Guid.Empty || r.ServiceId != serviceId
                || string.IsNullOrWhiteSpace(r.Actor) || r.Actor.Length > 256 || !Key(r.IdempotencyKey)
                || !Digest(r.Fingerprint) || !Digest(r.PayloadSha256) || r.Sequence <= 0 || r.PolicyRevision <= 0
                || !Guid.TryParseExact(r.Revision, "N", out _) || !document.Sources.ContainsKey(r.SourceId))
            || document.Receipts.Select(r => r.OperationId).Distinct().Count() != document.Receipts.Length
            || document.Receipts.Select(r => r.Actor + "\n" + r.IdempotencyKey).Distinct(StringComparer.Ordinal).Count() != document.Receipts.Length)
            throw new ModulePackageException("module-package-state-invalid");
        return (document, Hash(bytes));
    }

    internal Receipt Accept(byte[] envelope, ModulePackageTrustPolicy policy, ModulePackageContext context,
        string expectedRevision, string actor, string idempotencyKey, DateTimeOffset now)
    {
        if (context.ServiceId != _serviceId) throw new ModulePackageException("module-package-service-mismatch");
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || actor.Contains('\n') || !Key(idempotencyKey))
            throw new ModulePackageException("module-package-request-invalid");
        CheckUnchanged();
        var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(new { context.ServiceId, policy.SourceId, expectedRevision,
            envelopeSha256 = Hash(envelope) }, ModulePackageJson.Options));
        var prior = _document.Receipts.SingleOrDefault(r => r.Actor == actor && r.IdempotencyKey == idempotencyKey);
        if (prior is not null)
        {
            if (prior.Fingerprint != fingerprint) throw new ModulePackageException("module-package-idempotency-conflict");
            return prior;
        }
        if (expectedRevision != _document.Revision) throw new ModulePackageException("module-package-revision-conflict");
        var history = _document.Sources.TryGetValue(policy.SourceId, out var source) ? source.History
            : ModulePackageTrustHistory.Empty(policy, context);
        var verified = ModulePackageVerifier.VerifyCatalog(envelope, policy, context, history, now);
        var versions = new Dictionary<string, string>(_document.VersionIdentities, StringComparer.Ordinal);
        foreach (var release in verified.Catalog.Releases)
        {
            var m = release.Manifest;
            var versionKey = m.ModuleId + "/" + m.Version + "/" + m.Rid;
            var identity = Hash(JsonSerializer.SerializeToUtf8Bytes(new { release.Manifest, release.ManifestSha256,
                release.ArchiveSha256, release.ArchiveBytes }, ModulePackageJson.Options));
            if (versions.TryGetValue(versionKey, out var oldIdentity) && oldIdentity != identity)
                throw new ModulePackageException("module-package-cross-source-equivocation");
            versions[versionKey] = identity;
        }
        var revision = Guid.NewGuid().ToString("N");
        var receipt = new Receipt(Guid.NewGuid(), actor, idempotencyKey, fingerprint, _serviceId, policy.SourceId,
            revision, verified.Catalog.Sequence, verified.PayloadSha256, policy.Revision, now);
        var sources = new Dictionary<string, SavedSource>(_document.Sources, StringComparer.Ordinal)
        { [policy.SourceId] = new(Encoding.UTF8.GetString(envelope), verified.NextHistory) };
        var next = _document with { Revision = revision, Sources = sources, VersionIdentities = versions, Receipts = [.. _document.Receipts, receipt] };
        if (sources.Count > 16 || versions.Count > 10000 || next.Receipts.Length > 10000)
            throw new ModulePackageException("module-package-history-capacity");
        _ = GlobalRevocations(next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, ModulePackageJson.Options);
        if (bytes.Length > Limit) throw new ModulePackageException("module-package-history-capacity");
        Save(bytes);
        _document = next;
        _fileHash = Hash(bytes);
        return receipt;
    }

    internal static VerifiedModulePackageCatalog ReadCurrentCatalog(string contentRoot, ModulePackageTrustPolicy policy,
        ModulePackageContext context, DateTimeOffset now) => ReadCurrentCatalog(Read(contentRoot, context.ServiceId), policy, context, now);
    internal static VerifiedModulePackageCatalog ReadCurrentCatalog(Document document, ModulePackageTrustPolicy policy,
        ModulePackageContext context, DateTimeOffset now)
    {
        if (document.ServiceId != context.ServiceId) throw new ModulePackageException("module-package-service-mismatch");
        if (!document.Sources.TryGetValue(policy.SourceId, out var source)) throw new ModulePackageException("module-package-source-not-accepted");
        var result = ModulePackageVerifier.VerifyCatalog(Encoding.UTF8.GetBytes(source.Envelope), policy, context, source.History, now);
        if (result.NextHistory.PolicyRevision != source.History.PolicyRevision || result.NextHistory.PolicyHash != source.History.PolicyHash)
            throw new ModulePackageException("module-package-source-refresh-required");
        var revocations = GlobalRevocations(document);
        return result with { NextHistory = result.NextHistory with { RevokedReleaseIds = revocations.Ids.Order(StringComparer.Ordinal).ToArray(),
            RevokedDigests = revocations.Digests.Order(StringComparer.Ordinal).ToArray() } };
    }

    internal static void EnsureNotRevoked(Document document, string releaseId, string digest)
    {
        var revocations = GlobalRevocations(document);
        if (revocations.Ids.Contains(releaseId) || revocations.Digests.Contains(digest))
            throw new ModulePackageException("module-package-release-revoked");
    }
    private static (HashSet<string> Ids, HashSet<string> Digests) GlobalRevocations(Document document)
    {
        // A mirror must not resurrect bytes revoked by another accepted source. Keep provenance in
        // the persisted per-source evidence, and combine it only for candidate eligibility checks.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var saved in document.Sources.Values)
        {
            if (saved.History.RevokedReleaseIds is not { Length: <= 10000 }
                || saved.History.RevokedDigests is not { Length: <= 10000 }
                || saved.History.ReleaseDigests is not { Count: <= 10000 }
                || !saved.History.RevokedReleaseIds.All(Identifier) || !saved.History.RevokedDigests.All(Digest)
                || saved.History.ReleaseDigests.Any(p => !Identifier(p.Key) || !Digest(p.Value)))
                throw new ModulePackageException("module-package-state-invalid");
            ids.UnionWith(saved.History.RevokedReleaseIds);
            digests.UnionWith(saved.History.RevokedDigests);
            foreach (var id in saved.History.RevokedReleaseIds)
                if (saved.History.ReleaseDigests.TryGetValue(id, out var digest)) digests.Add(digest);
        }
        if (ids.Count > 10000 || digests.Count > 10000) throw new ModulePackageException("module-package-history-capacity");
        return (ids, digests);
    }

    private void Save(byte[] bytes)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteProtected(temporary, bytes);
            CheckUnchanged();
            if (!RegularFile(_marker)) WriteProtected(_marker, Encoding.UTF8.GetBytes(_serviceId));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void WriteProtected(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        if (OperatingSystem.IsLinux())
        {
            ModuleManagementStore.PreserveUnixOwner(path, _identityPath);
            stream.Flush(flushToDisk: true);
        }
    }
    private void CheckUnchanged()
    {
        if (_fileHash != ExistingHash()) throw new ModulePackageException("module-package-revision-conflict");
        if (_fileHash is null && RegularFile(_marker)) throw new ModulePackageException("module-package-state-recovery-required");
        if (_fileHash is not null && (!RegularFile(_marker) || Encoding.UTF8.GetString(ReadBounded(_marker, 256)) != _serviceId))
            throw new ModulePackageException("module-package-state-recovery-required");
    }
    private string? ExistingHash() => RegularFile(_path) ? Hash(ReadBounded(_path, Limit)) : null;
    private static bool RegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new ModulePackageException("module-package-state-path-invalid");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is 0 || stream.Length > limit) throw new ModulePackageException("module-package-state-size");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new ModulePackageException("module-package-state-size");
        return bytes;
    }
    private static bool Key(string? key) => key is { Length: > 0 and <= 128 }
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 96 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() { _hostLock?.Dispose(); _catalogLock.Dispose(); }
}
