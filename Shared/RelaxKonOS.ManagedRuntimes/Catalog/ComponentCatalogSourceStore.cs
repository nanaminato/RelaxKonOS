using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.ManagedRuntimes.Catalog;

/// <summary>Owner-scoped trust journal. Cache removal never removes source history or revocations.</summary>
public sealed class ComponentCatalogSourceStore : IDisposable
{
    private sealed record Saved(ComponentCatalogSourcePolicy Policy, long PolicyRevision, bool Removed,
        PackageCatalogKeys Keys, long Sequence, string? PayloadHash, long AcceptedPolicyRevision,
        string? Envelope, Dictionary<string, byte[]> Metadata, Dictionary<string, string> ReleaseDigests,
        string[] RevokedReleaseIds, string[] RevokedDigests, string? ProblemCode);
    private sealed record Document(int SchemaVersion, string Owner, string InstanceId, long Revision,
        Dictionary<string, Saved> Sources, Dictionary<string, string> VersionIdentities);
    private const int StateLimit = 32 * 1024 * 1024;
    private readonly string _root, _owner, _path, _marker;
    private readonly FileStream _fileLock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IComponentCatalogTransport _transport;
    private readonly TimeProvider _clock;
    private Document _state;
    private string? _hash;
    private bool _disposed;
    private bool _installLeaseActive;
    public event EventHandler? Changed;
    public string? RecoveryProblem { get; private set; }

    public ComponentCatalogSourceStore(string root, string owner, IComponentCatalogTransport? transport = null, TimeProvider? clock = null)
    {
        _root = Path.GetFullPath(root); _owner = owner;
        _path = Path.Combine(_root, "trust-state.json"); _marker = _root + ".initialized";
        _transport = transport ?? new ComponentCatalogHttpsTransport(); _clock = clock ?? TimeProvider.System;
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 256 || owner.Any(char.IsControl)) throw RemoteCatalogRules.Error("owner_invalid");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_root);
        else Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CheckPath(_root);
        var lockPath = Path.Combine(_root, "trust.lock");
        if (File.Exists(lockPath)) CheckPath(lockPath);
        _fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var hasState = File.Exists(_path); var hasMarker = File.Exists(_marker);
            if (!hasState && !hasMarker)
                _state = new(1, owner, Guid.NewGuid().ToString("N"), 0, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            else
            {
                if (!hasState || !hasMarker || Encoding.UTF8.GetString(ReadFile(_marker, 512)) != owner) throw RemoteCatalogRules.Error("recovery_required");
                var bytes = ReadFile(_path, StateLimit);
                _state = Read<Document>(bytes, StateLimit); _hash = RemoteCatalogRules.Hash(bytes);
                ValidateState();
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or RuntimePolicyException or FormatException or JsonException)
        {
            // Do not reset trust. Remote operations remain blocked, while bundled/local installation still works.
            RecoveryProblem = "component.catalog_recovery_required";
            _state = new(1, owner, Guid.NewGuid().ToString("N"), 0, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        }
        catch { _fileLock.Dispose(); throw; }
    }

    public long Revision { get { _gate.Wait(); try { CheckUnchanged(); return _state.Revision; } finally { _gate.Release(); } } }
    public IReadOnlyList<ComponentCatalogSourceView> Sources(bool includeRemoved = false)
    {
        _gate.Wait();
        try
        {
            CheckUnchanged();
            return _state.Sources.Values.Where(source => includeRemoved || !source.Removed).Select(source =>
            {
                var publication = Publication(source);
                var status = source.Removed ? "removed" : !source.Policy.Enabled ? "disabled" : publication is null ? "not-refreshed"
                    : source.AcceptedPolicyRevision != source.PolicyRevision ? "refresh-required"
                    : publication.ExpiresAt <= _clock.GetUtcNow() ? "expired" : source.ProblemCode is not null ? "refresh-failed" : "ready";
                return new ComponentCatalogSourceView(Clone(source.Policy), source.PolicyRevision, source.Sequence,
                    publication?.ExpiresAt, status, source.ProblemCode);
            }).OrderBy(source => source.Policy.SourceId, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Approval binds the displayed whole-policy fingerprint and the observed journal revision.</summary>
    public void Approve(ComponentCatalogSourcePolicy policy, long expectedRevision, string confirmedFingerprint)
    {
        policy = Clone(policy); policy.Validate();
        if (confirmedFingerprint != policy.Fingerprint) throw RemoteCatalogRules.Error("confirmation_required");
        _gate.Wait();
        try
        {
            CheckRevision(expectedRevision); CheckNoInstall();
            _state.Sources.TryGetValue(policy.SourceId, out var old);
            if (old is not null && (old.Policy.PublisherId != policy.PublisherId || old.Policy.Trust != policy.Trust))
                throw RemoteCatalogRules.Error("source_identity_changed");
            PackageCatalogKeys keys;
            try { keys = PackageCatalogSignature.CheckKeys(policy.PublicKeysSpki, old?.Keys.Hashes ?? new Dictionary<string, string>(), old?.Keys.RetiredIds ?? []); }
            catch (PackageCatalogVerificationException error) { throw RemoteCatalogRules.Error(error.Code.Replace('-', '_')); }
            var revision = checked(_state.Revision + 1);
            var saved = old is null ? new Saved(policy, revision, false, keys, 0, null, 0, null,
                new(StringComparer.Ordinal), new(StringComparer.Ordinal), [], [], null)
                : old with { Policy = policy, PolicyRevision = revision, Removed = false, Keys = keys, ProblemCode = null };
            Save(_state with { Revision = revision, Sources = new(_state.Sources, StringComparer.Ordinal) { [policy.SourceId] = saved } });
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetEnabled(string sourceId, bool enabled, long expectedRevision)
    {
        _gate.Wait();
        try
        {
            CheckRevision(expectedRevision); CheckNoInstall();
            var old = Require(sourceId);
            if (old.Removed || enabled) throw RemoteCatalogRules.Error("confirmation_required");
            var next = old with { Policy = old.Policy with { Enabled = false }, PolicyRevision = checked(_state.Revision + 1) };
            Save(_state with { Revision = _state.Revision + 1, Sources = new(_state.Sources, StringComparer.Ordinal) { [sourceId] = next } });
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string sourceId, long expectedRevision)
    {
        _gate.Wait();
        try
        {
            CheckRevision(expectedRevision); CheckNoInstall(); var old = Require(sourceId);
            var next = old with { Removed = true, Policy = old.Policy with { Enabled = false }, PolicyRevision = checked(_state.Revision + 1) };
            Save(_state with { Revision = _state.Revision + 1, Sources = new(_state.Sources, StringComparer.Ordinal) { [sourceId] = next } });
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(string sourceId, CancellationToken ct = default)
    {
        Saved old;
        _gate.Wait(ct);
        try { CheckUnchanged(); old = RequireEnabled(sourceId); }
        finally { _gate.Release(); }
        try
        {
            using var envelope = new MemoryStream();
            await _transport.CopyAsync(old.Policy, old.Policy.CatalogPath, RemoteCatalogRules.EnvelopeLimit, envelope, ct);
            var bytes = envelope.ToArray();
            var publication = VerifyPublication(old, bytes);
            var metadata = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            async Task Fetch(string path)
            {
                if (metadata.ContainsKey(path)) return;
                var resource = publication.Resources.SingleOrDefault(file => file.Path == path) ?? throw RemoteCatalogRules.Error("resource_missing");
                if (!path.EndsWith(".json", StringComparison.Ordinal) || resource.Bytes > 262144
                    || (total += resource.Bytes) > RemoteCatalogRules.MetadataLimit) throw RemoteCatalogRules.Error("metadata_size");
                using var output = new MemoryStream();
                await _transport.CopyAsync(old.Policy, path, resource.Bytes, output, ct);
                var content = output.ToArray(); VerifyResource(resource, content); metadata.Add(path, content);
            }
            await Fetch(publication.Catalog);
            var index = ComponentCatalogFormat.Read<ComponentCatalogIndex>(metadata[publication.Catalog]);
            if (index.SchemaVersion != 1 || index.Components is not { Count: <= 256 } || index.Workloads is null
                || index.Components.Any(entry => entry is null || !RemoteCatalogRules.Id(entry.Id) || string.IsNullOrEmpty(entry.Manifest)))
                throw RemoteCatalogRules.Error("metadata_invalid");
            foreach (var entry in index.Components)
                await Fetch(RemoteCatalogRules.Resolve(entry.Manifest, publication.Catalog));
            var catalog = ParseCatalog(publication, metadata);
            RemoteCatalogRules.ValidateCatalog(old.Policy, publication, catalog, metadata);
            _gate.Wait(ct);
            try
            {
                CheckUnchanged(); CheckNoInstall(); var current = RequireEnabled(sourceId);
                if (current.PolicyRevision != old.PolicyRevision || current.Sequence != old.Sequence || current.PayloadHash != old.PayloadHash)
                    throw RemoteCatalogRules.Error("revision_conflict");
                var versions = new Dictionary<string, string>(_state.VersionIdentities, StringComparer.Ordinal);
                var releaseDigests = new Dictionary<string, string>(current.ReleaseDigests, StringComparer.Ordinal);
                foreach (var component in catalog.Components)
                    foreach (var target in component.Targets.Where(target => target.Artifact.Reference is not null))
                        foreach (var arch in target.Architectures)
                        {
                            var identity = RemoteCatalogRules.ReleaseIdentity(component, target, arch);
                            var key = old.Policy.PublisherId + "/" + identity;
                            var hash = RemoteCatalogRules.Hash(JsonSerializer.SerializeToUtf8Bytes(new { component, target.Artifact.Sha256 }, ModulePackageJson.Options));
                            if (versions.TryGetValue(key, out var prior) && prior != hash) throw RemoteCatalogRules.Error("version_equivocation");
                            versions[key] = hash; releaseDigests[identity] = target.Artifact.Sha256!;
                        }
                var next = current with { Sequence = publication.Sequence, PayloadHash = PayloadHash(bytes), Envelope = Convert.ToBase64String(bytes),
                    AcceptedPolicyRevision = current.PolicyRevision, Metadata = metadata, ReleaseDigests = releaseDigests,
                    RevokedReleaseIds = current.RevokedReleaseIds.Union(publication.RevokedReleaseIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    RevokedDigests = current.RevokedDigests.Union(publication.RevokedDigests, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), ProblemCode = null };
                Save(_state with { Revision = checked(_state.Revision + 1), Sources = new(_state.Sources, StringComparer.Ordinal) { [sourceId] = next }, VersionIdentities = versions });
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _gate.Wait();
            try
            {
                CheckUnchanged();
                if (_state.Sources.TryGetValue(sourceId, out var current) && current.PolicyRevision == old.PolicyRevision)
                    Save(_state with { Revision = checked(_state.Revision + 1), Sources = new(_state.Sources, StringComparer.Ordinal)
                    { [sourceId] = current with { ProblemCode = error is RuntimePolicyException policy ? policy.ProblemCode : "component.catalog_refresh_failed" } } });
            }
            finally { _gate.Release(); }
            throw;
        }
        finally { Changed?.Invoke(this, EventArgs.Empty); }
    }

    public ComponentCatalogSourceSnapshot Snapshot(string sourceId, bool forInstall = false)
    {
        _gate.Wait();
        try { CheckUnchanged(); return SnapshotLocked(sourceId, forInstall); }
        finally { _gate.Release(); }
    }
    private ComponentCatalogSourceSnapshot SnapshotLocked(string sourceId, bool forInstall)
    {
        var saved = forInstall ? RequireEnabled(sourceId) : Require(sourceId);
        var publication = Publication(saved) ?? throw RemoteCatalogRules.Error("not_refreshed");
        if (forInstall)
        {
            if (saved.AcceptedPolicyRevision != saved.PolicyRevision) throw RemoteCatalogRules.Error("refresh_required");
            _ = VerifyPublication(saved, Convert.FromBase64String(saved.Envelope!));
        }
        var metadata = saved.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        var catalog = ParseCatalog(publication, metadata);
        RemoteCatalogRules.ValidateCatalog(saved.Policy, publication, catalog, metadata);
        return new(Clone(saved.Policy), saved.PolicyRevision, publication, catalog, metadata);
    }

    public ComponentCatalogInstallAuthorization Authorize(string sourceId, string componentId, ComponentHostEnvironment host)
    {
        _gate.Wait();
        try
        {
            CheckUnchanged(); var snapshot = SnapshotLocked(sourceId, true);
            var component = snapshot.Catalog.Components.SingleOrDefault(component => component.Id == componentId) ?? throw RemoteCatalogRules.Error("component_missing");
            var compatible = CompatibilityResolver.Resolve(component, host.Os, host.Architecture,
                target => snapshot.Policy.InstallerProviders.Contains(target.Installer.Type, StringComparer.Ordinal) && target.Installer.Type == "roapp");
            var target = compatible.Target ?? throw RemoteCatalogRules.Error("provider_blocked");
            var auth = new ComponentCatalogInstallAuthorization(sourceId, snapshot.Policy.PublisherId, snapshot.PolicyRevision,
                snapshot.Policy.Fingerprint, snapshot.Publication.Sequence, RemoteCatalogRules.ReleaseIdentity(component, target, host.Architecture), target.Artifact.Sha256!, _state.InstanceId);
            CheckAuthorization(auth);
            return auth;
        }
        finally { _gate.Release(); }
    }

    public IComponentCatalogInstallLease AcquireInstallLease(ComponentCatalogInstallAuthorization authorization)
    {
        _gate.Wait();
        try
        {
            CheckUnchanged(); CheckNoInstall(); CheckAuthorization(authorization); _installLeaseActive = true;
            // Keep authority stable without holding the state mutex across async UI/domain installation.
            // Reads must remain available; policy mutations fail explicitly while the lease is active.
            return new Lease(() =>
            {
                _gate.Wait(); try { CheckUnchanged(); CheckAuthorization(authorization); } finally { _gate.Release(); }
            }, () => { _gate.Wait(); try { _installLeaseActive = false; } finally { _gate.Release(); } });
        }
        finally { _gate.Release(); }
    }
    private void CheckNoInstall() { if (_installLeaseActive) throw RemoteCatalogRules.Error("installation_in_progress"); }
    private void CheckAuthorization(ComponentCatalogInstallAuthorization authorization)
    {
        var source = RequireEnabled(authorization.SourceId);
        if (_state.InstanceId != authorization.StoreInstanceId || source.PolicyRevision != authorization.PolicyRevision || source.Policy.Fingerprint != authorization.PolicyFingerprint
            || source.Sequence != authorization.CatalogSequence || source.AcceptedPolicyRevision != source.PolicyRevision
            || source.Policy.PublisherId != authorization.PublisherId || !source.Policy.InstallerProviders.Contains("roapp", StringComparer.Ordinal)
            || !source.ReleaseDigests.TryGetValue(authorization.ReleaseIdentity, out var digest) || digest != authorization.ArtifactSha256)
            throw RemoteCatalogRules.Error("authorization_changed");
        _ = VerifyPublication(source, Convert.FromBase64String(source.Envelope!));
        foreach (var record in _state.Sources.Values.Where(record => record.Policy.PublisherId == source.Policy.PublisherId))
        {
            if (record.RevokedReleaseIds.Contains(authorization.ReleaseIdentity, StringComparer.Ordinal)
                || record.RevokedDigests.Contains(authorization.ArtifactSha256, StringComparer.Ordinal)
                || record.RevokedReleaseIds.Any(id => record.ReleaseDigests.TryGetValue(id, out var revokedDigest) && revokedDigest == authorization.ArtifactSha256))
                throw RemoteCatalogRules.Error("release_revoked");
        }
    }

    internal async Task<Stream> OpenArtifactAsync(string sourceId, string reference, string declaringFile, CancellationToken ct)
    {
        var snapshot = Snapshot(sourceId, true);
        var path = RemoteCatalogRules.Resolve(reference, declaringFile);
        var resource = snapshot.Publication.Resources.SingleOrDefault(file => file.Path == path)
            ?? throw RemoteCatalogRules.Error("resource_missing");
        if (!path.EndsWith(".roapp", StringComparison.Ordinal) || resource.Bytes > RemoteCatalogRules.ArtifactLimit)
            throw RemoteCatalogRules.Error("artifact_invalid");
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cache); CheckPath(cache);
        var target = Path.Combine(cache, resource.Sha256 + ".roapp");
        if (!File.Exists(target))
        {
            var temporary = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var output = CreateProtected(temporary))
                {
                    await _transport.CopyAsync(snapshot.Policy, path, resource.Bytes, output, ct);
                    output.Flush(true);
                }
                await using (var input = OpenChecked(temporary)) await VerifyResourceAsync(resource, input, ct);
                var latest = Snapshot(sourceId, true);
                if (latest.PolicyRevision != snapshot.PolicyRevision || latest.Publication.Sequence != snapshot.Publication.Sequence)
                    throw RemoteCatalogRules.Error("authorization_changed");
                File.Move(temporary, target, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var stream = OpenChecked(target);
        try { await VerifyResourceAsync(resource, stream, ct); stream.Position = 0; return stream; }
        catch { stream.Dispose(); throw; }
    }

    private ComponentCatalogPublication VerifyPublication(Saved saved, byte[] envelopeBytes)
    {
        var envelope = Read<SignedComponentCatalog>(envelopeBytes, RemoteCatalogRules.EnvelopeLimit);
        if (envelope.SchemaVersion != 1) throw RemoteCatalogRules.Error("signature_envelope_invalid");
        VerifiedPackageCatalogSignature verified;
        try { verified = PackageCatalogSignature.Verify(envelope.KeyId, envelope.Payload, envelope.Signature, saved.Policy.PublicKeysSpki,
            saved.Keys.Hashes, saved.Keys.RetiredIds, RemoteCatalogRules.SignatureContext, RemoteCatalogRules.PayloadLimit); }
        catch (PackageCatalogVerificationException error) { throw RemoteCatalogRules.Error(error.Code.Replace('-', '_')); }
        var publication = Read<ComponentCatalogPublication>(verified.Payload, RemoteCatalogRules.PayloadLimit);
        var now = _clock.GetUtcNow();
        if (publication.SchemaVersion != 1 || publication.Kind != "component-catalog" || publication.SourceId != saved.Policy.SourceId
            || publication.PublisherId != saved.Policy.PublisherId || publication.Sequence <= 0 || publication.Catalog != "catalog.json"
            || publication.Resources is not { Length: > 0 and <= 4096 } || publication.RevokedReleaseIds is not { Length: <= 10000 }
            || publication.RevokedDigests is not { Length: <= 10000 } || publication.RevokedReleaseIds.Any(id => !RemoteCatalogRules.Digest(id))
            || publication.RevokedDigests.Any(digest => !RemoteCatalogRules.Digest(digest))) throw RemoteCatalogRules.Error("publication_invalid");
        if (publication.Sequence < saved.Sequence || publication.Sequence == saved.Sequence && RemoteCatalogRules.Hash(verified.Payload) != saved.PayloadHash)
            throw RemoteCatalogRules.Error("replayed");
        if (publication.IssuedAt.Offset != TimeSpan.Zero || publication.ExpiresAt.Offset != TimeSpan.Zero
            || publication.IssuedAt > now || publication.ExpiresAt <= now || publication.ExpiresAt <= publication.IssuedAt
            || (publication.ExpiresAt - publication.IssuedAt).TotalSeconds > saved.Policy.MaximumValiditySeconds)
            throw RemoteCatalogRules.Error("expired");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in publication.Resources)
            if (file is null || !RemoteCatalogRules.Path(file.Path) || !paths.Add(file.Path) || !RemoteCatalogRules.Digest(file.Sha256)
                || file.Bytes is <= 0 or > RemoteCatalogRules.ArtifactLimit) throw RemoteCatalogRules.Error("resource_invalid");
        return publication;
    }

    private static ResolvedComponentCatalog ParseCatalog(ComponentCatalogPublication publication, IReadOnlyDictionary<string, byte[]> metadata)
    {
        byte[] ReadMetadata(string reference, string declaring)
        {
            var path = RemoteCatalogRules.Resolve(reference, declaring);
            var file = publication.Resources.SingleOrDefault(file => file.Path == path) ?? throw RemoteCatalogRules.Error("resource_missing");
            if (!metadata.TryGetValue(path, out var bytes)) throw RemoteCatalogRules.Error("metadata_missing");
            VerifyResource(file, bytes); return bytes;
        }
        return ComponentCatalogFormat.Load(ReadMetadata, (reference, declaring) =>
        {
            if (!publication.Resources.Any(file => file.Path == RemoteCatalogRules.Resolve(reference, declaring)))
                throw RemoteCatalogRules.Error("resource_missing");
        });
    }
    private static void VerifyResource(ComponentCatalogResource resource, byte[] bytes)
    { if (bytes.LongLength != resource.Bytes || RemoteCatalogRules.Hash(bytes) != resource.Sha256) throw RemoteCatalogRules.Error("digest_mismatch"); }
    private static async Task VerifyResourceAsync(ComponentCatalogResource resource, Stream stream, CancellationToken ct)
    {
        if (stream.Length != resource.Bytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != resource.Sha256)
            throw RemoteCatalogRules.Error("digest_mismatch");
    }
    private static string PayloadHash(byte[] bytes) => RemoteCatalogRules.Hash(Convert.FromBase64String(Read<SignedComponentCatalog>(bytes, RemoteCatalogRules.EnvelopeLimit).Payload));
    private static ComponentCatalogPublication? Publication(Saved source) => source.Envelope is null ? null :
        Read<ComponentCatalogPublication>(Convert.FromBase64String(Read<SignedComponentCatalog>(Convert.FromBase64String(source.Envelope), RemoteCatalogRules.EnvelopeLimit).Payload), RemoteCatalogRules.PayloadLimit);
    private Saved Require(string sourceId) => _state.Sources.GetValueOrDefault(sourceId) ?? throw RemoteCatalogRules.Error("source_unknown");
    private Saved RequireEnabled(string sourceId)
    {
        var saved = Require(sourceId);
        if (saved.Removed || !saved.Policy.Enabled) throw RemoteCatalogRules.Error("source_disabled");
        return saved;
    }
    private static ComponentCatalogSourcePolicy Clone(ComponentCatalogSourcePolicy policy) =>
        Read<ComponentCatalogSourcePolicy>(JsonSerializer.SerializeToUtf8Bytes(policy, ModulePackageJson.Options), 65536);
    private static T Read<T>(ReadOnlyMemory<byte> bytes, int limit)
    {
        try { return ModulePackageJson.Read<T>(bytes, limit); }
        catch (ModulePackageException) { throw RemoteCatalogRules.Error("metadata_invalid"); }
    }
    private void CheckRevision(long expected) { CheckUnchanged(); if (expected != _state.Revision) throw RemoteCatalogRules.Error("revision_conflict"); }
    private void CheckUnchanged()
    {
        if (RecoveryProblem is not null) throw RemoteCatalogRules.Error("recovery_required");
        if (_hash is null ? File.Exists(_path) || File.Exists(_marker)
            : !File.Exists(_path) || !File.Exists(_marker) || RemoteCatalogRules.Hash(ReadFile(_path, StateLimit)) != _hash
                || Encoding.UTF8.GetString(ReadFile(_marker, 512)) != _owner) throw RemoteCatalogRules.Error("recovery_required");
    }
    private void ValidateState()
    {
        if (_state.SchemaVersion != 1 || _state.Owner != _owner || !Guid.TryParseExact(_state.InstanceId, "N", out _)
            || _state.Revision < 1 || _state.Sources is not { Count: > 0 and <= 16 } || _state.VersionIdentities is not { Count: <= 10000 }
            || _state.VersionIdentities.Any(pair => pair.Key.Length > 512 || !RemoteCatalogRules.Digest(pair.Value)))
            throw RemoteCatalogRules.Error("state_invalid");
        foreach (var pair in _state.Sources)
        {
            var saved = pair.Value;
            if (saved is null || saved.Policy is null || pair.Key != saved.Policy.SourceId || saved.PolicyRevision < 1
                || saved.PolicyRevision > _state.Revision || saved.Sequence < 0 || saved.AcceptedPolicyRevision > saved.PolicyRevision
                || saved.Metadata is not { Count: <= 257 } || saved.Keys?.Hashes is not { Count: <= 256 }
                || saved.Keys.RetiredIds is not { Length: <= 256 } || saved.ReleaseDigests is not { Count: <= 10000 }
                || saved.RevokedDigests is not { Length: <= 10000 } || saved.RevokedReleaseIds is not { Length: <= 10000 }
                || saved.ReleaseDigests.Any(item => !RemoteCatalogRules.Digest(item.Key) || !RemoteCatalogRules.Digest(item.Value))
                || saved.RevokedDigests.Any(digest => !RemoteCatalogRules.Digest(digest)) || saved.RevokedReleaseIds.Any(id => !RemoteCatalogRules.Digest(id))
                || (saved.Sequence == 0 ? saved.Envelope is not null || saved.PayloadHash is not null : saved.Envelope is null || !RemoteCatalogRules.Digest(saved.PayloadHash)))
                throw RemoteCatalogRules.Error("state_invalid");
            saved.Policy.Validate();
            try { PackageCatalogSignature.CheckKeys(saved.Policy.PublicKeysSpki, saved.Keys.Hashes, saved.Keys.RetiredIds); }
            catch (PackageCatalogVerificationException) { throw RemoteCatalogRules.Error("state_invalid"); }
            if (saved.Envelope is not null)
            {
                var publication = Publication(saved)!;
                if (PayloadHash(Convert.FromBase64String(saved.Envelope)) != saved.PayloadHash || publication.Sequence != saved.Sequence
                    || publication.SourceId != saved.Policy.SourceId || publication.PublisherId != saved.Policy.PublisherId
                    || publication.Kind != "component-catalog" || publication.SchemaVersion != 1
                    || publication.RevokedReleaseIds.Except(saved.RevokedReleaseIds, StringComparer.Ordinal).Any()
                    || publication.RevokedDigests.Except(saved.RevokedDigests, StringComparer.Ordinal).Any())
                    throw RemoteCatalogRules.Error("state_invalid");
                _ = ParseCatalog(publication, saved.Metadata);
            }
        }
    }
    private void Save(Document next)
    {
        if (next.Sources.Count > 16 || next.VersionIdentities.Count > 10000 || next.Sources.Values.Any(source =>
            source.ReleaseDigests.Count > 10000 || source.RevokedReleaseIds.Length > 10000 || source.RevokedDigests.Length > 10000))
            throw RemoteCatalogRules.Error("history_capacity");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, ModulePackageJson.Options);
        if (bytes.Length > StateLimit) throw RemoteCatalogRules.Error("history_capacity");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = CreateProtected(temporary)) { stream.Write(bytes); stream.Flush(true); }
            CheckUnchanged();
            if (!File.Exists(_marker)) using (var marker = CreateProtected(_marker)) { marker.Write(Encoding.UTF8.GetBytes(_owner)); marker.Flush(true); }
            File.Move(temporary, _path, overwrite: true);
            _state = next; _hash = RemoteCatalogRules.Hash(bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static FileStream CreateProtected(string path)
    {
        CheckPath(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
    private static FileStream OpenChecked(string path) { CheckPath(path); return new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
    private static byte[] ReadFile(string path, int limit)
    {
        using var input = OpenChecked(path);
        if (input.Length > limit) throw RemoteCatalogRules.Error("state_invalid");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw RemoteCatalogRules.Error("path_invalid");
    }
    private sealed class Lease(Action validate, Action release) : IComponentCatalogInstallLease
    {
        private Action? _release = release;
        public void Revalidate() { if (_release is null) throw new ObjectDisposedException(nameof(Lease)); validate(); }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _fileLock.Dispose(); _gate.Dispose(); }
}

public sealed class HttpCatalogSource(ComponentCatalogSourceStore store, string sourceId) : IAuthenticatedComponentCatalogSource
{
    private ComponentCatalogSourceSnapshot? _snapshot;
    public string SourceId { get; } = sourceId;
    public ResolvedComponentCatalog Load() => (_snapshot = store.Snapshot(SourceId, true)).Catalog;
    public Task RefreshAsync(CancellationToken ct = default) => store.RefreshAsync(SourceId, ct);
    public string ManifestReference(string componentId)
    {
        var snapshot = _snapshot ?? throw RemoteCatalogRules.Error("not_refreshed");
        var index = ComponentCatalogFormat.Read<ComponentCatalogIndex>(snapshot.Metadata[snapshot.Publication.Catalog]);
        return RemoteCatalogRules.Resolve(index.Components.Single(entry => entry.Id == componentId).Manifest, snapshot.Publication.Catalog);
    }
    public Task<Stream> OpenAsync(string reference, string declaringFile, CancellationToken ct = default) =>
        store.OpenArtifactAsync(SourceId, reference, declaringFile, ct);
    public ComponentCatalogInstallAuthorization Authorize(string componentId, ComponentHostEnvironment host)
    {
        var authorization = store.Authorize(SourceId, componentId, host);
        if (_snapshot is null || authorization.PolicyRevision != _snapshot.PolicyRevision || authorization.CatalogSequence != _snapshot.Publication.Sequence)
            throw RemoteCatalogRules.Error("authorization_changed");
        return authorization;
    }
}
