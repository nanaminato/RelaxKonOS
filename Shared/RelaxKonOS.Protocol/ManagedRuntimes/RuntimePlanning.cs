namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// Pure, fail-closed admission checks. Call only with host-owned snapshots under the instance lock.
/// <paramref name="dependencies"/> is the Host's own measurement for the requested release; native
/// validation, authorization and rollback recoverability remain adapter gates.
/// </summary>
public static class RuntimePlanning
{
    public static RuntimePlanResult Create(RuntimePlanRequest request, RuntimeInstallation installation,
        RuntimeSourceIdentity source, RuntimeCatalog catalog, long acceptedCatalogRevision,
        DateTimeOffset now, RuntimeDependencyReport? dependencies)
    {
        var problems = new List<string>();
        void Reject(bool condition, string code) { if (condition) problems.Add("runtime." + code); }

        Reject(request.InstallationId == Guid.Empty || request.InstallationId != installation.InstallationId
            || string.IsNullOrWhiteSpace(request.HostId) || request.HostId != installation.HostId, "installation_mismatch");
        Reject(string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128, "idempotency_key_invalid");
        Reject(!Enum.IsDefined(request.Change), "action_invalid");
        Reject(request.ExpectedStateRevision != installation.StateRevision, "state_revision_conflict");
        Reject(installation.Ownership == RuntimeOwnership.Unknown || !Enum.IsDefined(installation.Ownership), "state_unknown");
        Reject(installation.Ownership == RuntimeOwnership.External, "external_installation");
        Reject(installation.RecoveryRequired, "recovery_required");
        Reject(request.Change == RuntimeChange.Install && installation.Ownership != RuntimeOwnership.Absent, "already_installed");
        Reject(request.Change != RuntimeChange.Install && installation.Ownership != RuntimeOwnership.Managed, "not_managed");

        // Removal must remain possible when a source has expired or has been revoked.
        if (request.Change == RuntimeChange.Uninstall)
        {
            Reject(request.ReleaseId is not null, "release_unexpected");
            return Result(null);
        }

        Reject(!source.Enabled || source.Trust != RuntimeTrust.Confirmed, "source_untrusted");
        Reject(source.SourceId != catalog.SourceId, "source_mismatch");
        Reject(request.ExpectedTrustRevision != source.TrustRevision || catalog.TrustRevision != source.TrustRevision,
            "trust_revision_conflict");
        Reject(request.ExpectedCatalogRevision != catalog.Revision, "catalog_revision_conflict");
        Reject(catalog.Revision < acceptedCatalogRevision, "catalog_replayed");
        Reject(catalog.ExpiresAt <= now, "catalog_expired");
        Reject(!catalog.SignatureVerified, "catalog_unverified");
        var matches = catalog.Releases.Where(r => r.ReleaseId == request.ReleaseId).ToArray();
        Reject(string.IsNullOrWhiteSpace(request.ReleaseId) || matches.Length != 1, "release_unknown_or_ambiguous");
        if (matches.Length != 1) return Result(null);
        var release = matches[0];
        Reject(release.SourceId != source.SourceId || release.RuntimeId != installation.RuntimeId, "release_identity_mismatch");
        Reject(release.Trust != RuntimeTrust.Confirmed, "release_untrusted");
        Reject(release.Target != installation.Target, "target_unsupported");
        Reject(!Enum.IsDefined(release.Channel) || release.Channel == RuntimeChannel.Prerelease && !request.AllowPrerelease,
            "channel_not_allowed");
        Reject(!release.ConfigurationSupported || string.IsNullOrWhiteSpace(release.ConfigurationCapability), "configuration_unsupported");
        Reject(string.IsNullOrWhiteSpace(release.OriginalVersion) || string.IsNullOrWhiteSpace(release.SortVersion), "version_invalid");
        var artifact = release.Artifact;
        Reject(string.IsNullOrWhiteSpace(artifact.ArtifactId) || artifact.Sha256.Length != 64
            || !artifact.Sha256.All(Uri.IsHexDigit) || artifact.MaximumBytes <= 0
            || string.IsNullOrWhiteSpace(artifact.VerificationIdentity)
            || artifact.Format is null || !RuntimeArchiveRules.Formats.Contains(artifact.Format)
            || !RuntimeArchiveRules.Valid(artifact), "artifact_invalid");
        Reject(request.Change == RuntimeChange.Repair && release.ReleaseId != installation.ActiveReleaseId, "repair_target_mismatch");
        Reject(request.Change is RuntimeChange.Update or RuntimeChange.Rollback && release.ReleaseId == installation.ActiveReleaseId,
            "target_unchanged");
        // Only the Host can measure its own platform, service host, free space and kernel features. A missing
        // report is not "probably fine": it means nothing was measured for this release.
        Reject(dependencies is null || !dependencies.Satisfies(RuntimeArchiveDependencies.For(release)), "dependencies_unsatisfied");
        return Result(release);

        RuntimePlanResult Result(RuntimeRelease? release) => problems.Count == 0
            ? new(new(request, installation.RuntimeId, release, installation.ActiveReleaseId), [], dependencies)
            : new(null, problems.ToArray(), dependencies);
    }
}
