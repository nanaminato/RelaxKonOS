using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

public sealed record RuntimeSourceAudit(long TrustRevision, string Actor, DateTimeOffset At, string Fingerprint);
public sealed record StoredRuntimeSource(RuntimeSource Source, CatalogHistory History,
    VerifiedRuntimeCatalog? Cached, RuntimeSourceAudit[] Audit);
public sealed record AuthorizedRuntimeRelease(RuntimeSource Source, ManifestRelease Release, long CatalogRevision,
    RuntimeRelease CatalogRelease);

/// <summary>
/// Host-owned disk ledger. The host MUST provide OS protection before any access; a Server-writable
/// directory is not a Helper trust store. All readers/writers share the same on-disk lock.
/// </summary>
public sealed class RuntimeSourceStore(string directory, Action<string> protectDirectory)
{
    private const int MaximumStateBytes = 32 * 1024 * 1024;

    public StoredRuntimeSource? Read(string sourceId)
    {
        using var gate = Lock();
        return ReadCore(sourceId);
    }

    public StoredRuntimeSource[] List()
    {
        using var gate = Lock();
        return Directory.EnumerateFiles(directory, "*.source.json")
            .Select(path => Path.GetFileName(path)[..^12])
            .Select(id => ReadCore(id) ?? throw new RuntimePolicyException("runtime.source_state_invalid"))
            .OrderBy(s => s.Source.SourceId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Local admin path only. Confirmation binds the whole policy, not just the public key.</summary>
    public StoredRuntimeSource Confirm(RuntimeSource candidate, long expectedTrustRevision,
        string confirmationFingerprint, string actor, DateTimeOffset now)
    {
        candidate.Validate();
        if (candidate.ConfirmationFingerprint != confirmationFingerprint)
            throw new RuntimePolicyException("runtime.source_confirmation_required");
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256)
            throw new RuntimePolicyException("runtime.source_actor_invalid");
        using var gate = Lock();
        var previous = ReadCore(candidate.SourceId);
        if ((previous?.Source.TrustRevision ?? 0) != expectedTrustRevision || expectedTrustRevision < 0)
            throw new RuntimePolicyException("runtime.trust_revision_conflict");
        if (previous is not null && (previous.Source.RuntimeId != candidate.RuntimeId || previous.Source.Publisher != candidate.Publisher))
            throw new RuntimePolicyException("runtime.source_identity_immutable");
        if (previous is null && Directory.EnumerateFiles(directory, "*.source.json").Take(64).Count() >= 64)
            throw new RuntimePolicyException("runtime.source_limit");
        var accepted = candidate with { TrustRevision = checked(expectedTrustRevision + 1) };
        var audit = new RuntimeSourceAudit(accepted.TrustRevision, actor, now, candidate.ConfirmationFingerprint);
        var state = new StoredRuntimeSource(accepted, previous?.History ?? CatalogHistory.Empty,
            previous?.Cached, [.. (previous?.Audit ?? []), audit]);
        // Keep the full local audit; refuse a write if state exceeds the bounded ledger size.
        WriteCore(candidate.SourceId, state);
        return state;
    }

    public VerifiedRuntimeCatalog Accept(string sourceId, long expectedTrustRevision, ReadOnlyMemory<byte> signedEnvelope,
        DateTimeOffset now, Func<ManifestRelease, RuntimeConfigurationSupport> support)
    {
        using var gate = Lock();
        var previous = ReadCore(sourceId) ?? throw new RuntimePolicyException("runtime.source_not_found");
        if (previous.Source.TrustRevision != expectedTrustRevision)
            throw new RuntimePolicyException("runtime.trust_revision_conflict");
        var verified = RuntimeCatalogVerifier.Verify(previous.Source, signedEnvelope, previous.History, now, support);
        WriteCore(sourceId, previous with { History = verified.History, Cached = verified });
        return verified;
    }

    /// <summary>Resolve only from the protected ledger, never from a caller-supplied artifact, URL or digest.</summary>
    public AuthorizedRuntimeRelease AuthorizeRelease(string sourceId, string releaseId, long expectedCatalogRevision,
        long expectedTrustRevision, RuntimeTarget target, bool allowPrerelease, DateTimeOffset now)
    {
        using var gate = Lock();
        var state = ReadCore(sourceId) ?? throw new RuntimePolicyException("runtime.source_not_found");
        if (!state.Source.Enabled) throw new RuntimePolicyException("runtime.source_disabled");
        if (state.Source.TrustRevision != expectedTrustRevision) throw new RuntimePolicyException("runtime.trust_revision_conflict");
        var cached = state.Cached ?? throw new RuntimePolicyException("runtime.catalog_missing");
        if (cached.Catalog.TrustRevision != expectedTrustRevision) throw new RuntimePolicyException("runtime.trust_revision_conflict");
        if (cached.Catalog.Revision != expectedCatalogRevision) throw new RuntimePolicyException("runtime.catalog_revision_conflict");
        if (cached.Catalog.ExpiresAt <= now) throw new RuntimePolicyException("runtime.catalog_expired");
        if (!cached.Catalog.SignatureVerified) throw new RuntimePolicyException("runtime.catalog_unverified");
        var visible = cached.Catalog.Releases.Where(r => r.ReleaseId == releaseId).ToArray();
        var artifacts = cached.Artifacts.Where(r => r.ReleaseId == releaseId).ToArray();
        if (visible.Length != 1 || artifacts.Length != 1) throw new RuntimePolicyException("runtime.release_unknown_or_ambiguous");
        var release = visible[0];
        if (release.Channel == RuntimeChannel.Prerelease && !allowPrerelease)
            throw new RuntimePolicyException("runtime.channel_not_allowed");
        if (release.Trust != RuntimeTrust.Confirmed || state.History.RevokedReleaseIds.Contains(releaseId)
            || state.History.ConflictedReleaseIds.Contains(releaseId)
            || state.History.RevokedDigests.Contains(release.Artifact.Sha256, StringComparer.OrdinalIgnoreCase))
            throw new RuntimePolicyException("runtime.release_untrusted");
        if (release.Target != target || artifacts[0].Target != target) throw new RuntimePolicyException("runtime.target_unsupported");
        if (!release.ConfigurationSupported || string.IsNullOrWhiteSpace(release.ConfigurationCapability))
            throw new RuntimePolicyException("runtime.configuration_unsupported");
        if (release.RuntimeId != state.Source.RuntimeId || artifacts[0].RuntimeId != state.Source.RuntimeId
            || !RuntimeArchiveRules.Valid(release.Artifact)
            || !RuntimeJson.Serialize(release.Artifact).AsSpan().SequenceEqual(RuntimeJson.Serialize(artifacts[0].Artifact)))
            throw new RuntimePolicyException("runtime.source_state_invalid");
        return new(state.Source, artifacts[0], cached.Catalog.Revision, release);
    }

    private FileStream Lock()
    {
        RequireNoLinks(directory);
        protectDirectory(directory);
        RequireNoLinks(directory);
        var path = Path.Combine(directory, "registry.lock");
        RequireNoLinks(path);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(path, options); }
        catch (IOException) { throw new RuntimePolicyException("runtime.source_store_busy"); }
    }

    private StoredRuntimeSource? ReadCore(string sourceId)
    {
        var path = SourcePath(sourceId);
        RequireNoLinks(path);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumStateBytes) throw new RuntimePolicyException("runtime.source_state_invalid");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        var state = RuntimeJson.Read<StoredRuntimeSource>(bytes);
        state.Source.Validate();
        if (state.Source.SourceId != sourceId || state.Source.TrustRevision < 1
            || state.History.HighWater < 0 || state.Audit.Length == 0
            || state.Audit[^1].TrustRevision != state.Source.TrustRevision
            || state.Cached is { } cached && (cached.Catalog.SourceId != sourceId
                || cached.Catalog.Revision != state.History.HighWater || cached.PayloadHash != state.History.PayloadHash))
            throw new RuntimePolicyException("runtime.source_state_invalid");
        return state;
    }

    private void WriteCore(string sourceId, StoredRuntimeSource state)
    {
        var bytes = RuntimeJson.Serialize(state);
        if (bytes.Length > MaximumStateBytes) throw new RuntimePolicyException("runtime.source_state_too_large");
        var path = SourcePath(sourceId);
        RequireNoLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options)) { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    private string SourcePath(string sourceId)
    {
        if (!RuntimeValidation.Identifier(sourceId)) throw new RuntimePolicyException("runtime.source_invalid");
        return Path.Combine(directory, sourceId + ".source.json");
    }

    public static void RequireNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new RuntimePolicyException("runtime.linked_path_forbidden");
    }
}
