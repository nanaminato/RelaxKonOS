using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed record PreparedMihomoRuntime(PreparedRuntimeArtifact Artifact, MihomoBuildEvidence Build,
    string ConfigurationIdentity);

/// <summary>
/// Privileged preparation only: resolves the frozen release from protected trust, extracts verified
/// bytes and tests the current Server-generated configuration. Never registers or starts a service.
/// The caller holds its installation lock and supplies protected configuration, data and staging paths.
/// </summary>
internal sealed class MihomoRuntimePreparation(RuntimeSourceStore sources, IRuntimeArchiveTransport transport,
    RuntimeStageReporter? progress = null)
{
    internal static void RequireSupportedRelease(RuntimeRelease release)
    {
        if (release.RuntimeId != "mihomo" || !MihomoNativeValidation.TryTag(release.OriginalVersion, out var version)
            || MihomoRuntimeSupport.CapabilityFor(version.Normalized) is not { } capability
            || !release.ConfigurationSupported || release.ConfigurationCapability != capability
            || release.SortVersion != version.Normalized || release.Channel != RuntimeChannel.Stable
            || release.Target.Os != MihomoNativeValidation.PlatformName
            || release.Target.Architecture != MihomoNativeValidation.ArchitectureName
            || release.Target.InstallMode != RuntimeInstallMode.Archive
            || release.Target.ServiceHost != (OperatingSystem.IsWindows() ? "scm" : "systemd")
            || release.Artifact.EntryPoint != MihomoNativeValidation.BinaryName
            || !RuntimeArchiveRules.Valid(release.Artifact))
            throw new RuntimePolicyException("runtime.configuration_unsupported");
    }

    internal async Task<PreparedMihomoRuntime> PrepareAsync(RuntimeChangePlan plan, string protectedStagingRoot,
        string protectedDataDirectory, string protectedConfiguration, CancellationToken ct)
    {
        var release = plan.Release ?? throw new RuntimePolicyException("runtime.configuration_unsupported");
        if (plan.RuntimeId != "mihomo" || plan.Request.ReleaseId != release.ReleaseId
            || plan.Request.Change is not (RuntimeChange.Install or RuntimeChange.Update or RuntimeChange.Repair or RuntimeChange.Rollback))
            throw new RuntimePolicyException("runtime.release_untrusted");
        RequireSupportedRelease(release);
        Authorize(plan, release);
        var configuration = await ReadConfigurationAsync(protectedConfiguration, ct);
        var description = MihomoConfigurationPolicy.Describe(Encoding.UTF8.GetString(configuration));
        MihomoConfigurationPolicy.VerifyResources(description, protectedDataDirectory);
        RuntimeSourceStore.RequireNoLinks(protectedDataDirectory);
        if (!Directory.Exists(protectedDataDirectory)) throw new RuntimePolicyException("runtime.staging_root_missing");
        var prepared = await new RuntimeArtifactPreparer(sources, transport,
            progress is null ? null : stage => progress(stage, cancellable: true)).PrepareAsync(release.SourceId,
            release.ReleaseId, plan.Request.ExpectedCatalogRevision, plan.Request.ExpectedTrustRevision,
            release.Target, plan.Request.AllowPrerelease, protectedStagingRoot, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var build = MihomoNativeValidation.Parse(await MihomoOperations.VersionAsync(prepared.Archive.EntryPoint),
                release.OriginalVersion, release.Target.Os, release.Target.Architecture);
            if (description.Material.RequiredBuildTags.Any(tag => !build.BuildTags.Contains(tag)))
                throw new RuntimePolicyException("runtime.native_feature_missing");
            await MihomoOperations.TestConfigurationAsync(prepared.Archive.EntryPoint,
                protectedDataDirectory, protectedConfiguration);
            ct.ThrowIfCancellationRequested();
            // Native checks may take time or populate GEO data. They cannot renew the catalog grant,
            // silently substitute a changed configuration, or turn a revoked source into acceptance.
            Authorize(plan, release);
            var after = await ReadConfigurationAsync(protectedConfiguration, ct);
            if (!configuration.AsSpan().SequenceEqual(after))
                throw new RuntimePolicyException("runtime.configuration_drift");
            return new(prepared, build, Convert.ToHexString(SHA256.HashData(configuration)));
        }
        catch
        {
            RuntimeArchiveExtractor.Discard(prepared.Archive, protectedStagingRoot);
            throw;
        }
    }

    private void Authorize(RuntimeChangePlan plan, RuntimeRelease release)
    {
        var authorized = sources.AuthorizeRelease(release.SourceId, release.ReleaseId,
            plan.Request.ExpectedCatalogRevision, plan.Request.ExpectedTrustRevision, release.Target,
            plan.Request.AllowPrerelease, DateTimeOffset.UtcNow);
        if (!RuntimeJson.Serialize(release).AsSpan().SequenceEqual(RuntimeJson.Serialize(authorized.CatalogRelease)))
            throw new RuntimePolicyException("runtime.release_untrusted");
    }

    private static async Task<byte[]> ReadConfigurationAsync(string path, CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 or > 4 * 1024 * 1024)
            throw new RuntimePolicyException("runtime.native_configuration_invalid");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct);
        return bytes;
    }
}
