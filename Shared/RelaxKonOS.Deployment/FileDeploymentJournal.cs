using System.Security.Cryptography;
using System.Text.Json;

namespace RelaxKonOS.Deployment;

/// <summary>Stores checkpoint files in the caller's private deployment state directory.</summary>
public sealed class FileDeploymentJournal(string directory) : IDeploymentJournal
{
    public async Task<DeploymentPlan?> FindPendingPlanAsync(DeploymentPackageKey package, string version,
        string artifactIdentity, Guid accountId, string connectionIdentity, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return null;
        DeploymentPlan? latest = null;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var id)) continue;
            Entry entry;
            try { entry = await ReadAsync(path, cancellationToken); }
            catch (FileNotFoundException) { continue; }
            catch (JsonException) { continue; }
            catch (InvalidDataException) { continue; }
            var plan = entry.Plan;
            if (entry.Checkpoint.Verified || plan.Id != id || plan.OrderedReleases.IsDefaultOrEmpty
                || plan.ClientBinding is not { } client || client.AccountId != accountId
                || client.ConnectionIdentity != connectionIdentity) continue;
            var root = plan.OrderedReleases[^1];
            if (root.Package == package && root.Version == version && root.ArtifactIdentity == artifactIdentity
                && (latest is null || plan.ExpiresAt > latest.ExpiresAt))
                latest = plan;
        }
        return latest;
    }

    public async Task<DeploymentPlan?> FindStartedPlanAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, planId.ToString("N"));
        try
        {
            return (await ReadAsync(path, cancellationToken)).Plan;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task<IDeploymentLease> OpenAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, plan.Id.ToString("N"));
        var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        try
        {
            var identity = ComputeIdentity(plan);
            Entry? entry = null;
            if (File.Exists(path))
            {
                entry = await ReadAsync(path, cancellationToken);
                if (entry.Identity != identity)
                    throw new InvalidOperationException("deployment-plan-identity-changed");
            }
            return new Lease(fileLock, path, identity, plan, entry?.Checkpoint);
        }
        catch
        {
            await fileLock.DisposeAsync();
            throw;
        }
    }

    private static string ComputeIdentity(DeploymentPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            plan.Id,
            plan.Target,
            plan.ExpiresAt,
            plan.PreparedSteps,
            plan.ClientBinding,
            Releases = plan.OrderedReleases.Select(release => new
            {
                release.Package,
                release.Version,
                release.ArtifactIdentity,
                release.Satisfied,
                release.CurrentVersion,
                release.ActivationOnly,
                release.Requires,
                Capabilities = release.Capabilities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            })
        })));

    private static async Task<Entry> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var entry = await JsonSerializer.DeserializeAsync<Entry>(input, cancellationToken: cancellationToken);
        if (entry is not { Plan: not null, Checkpoint: not null }
            || entry.Plan.OrderedReleases.IsDefaultOrEmpty
            || entry.Plan.PreparedSteps.IsDefault
            || entry.Checkpoint.CompletedSteps < 0 || entry.Checkpoint.CompletedSteps > entry.Plan.OrderedReleases.Length
            || entry.Checkpoint.Verified && entry.Checkpoint.CompletedSteps != entry.Plan.OrderedReleases.Length
            || entry.Plan.OrderedReleases.Any(release => release is null || release.Capabilities is null || release.Requires.IsDefault))
            throw new InvalidDataException("Invalid deployment checkpoint.");
        if (!string.Equals(entry.Identity, ComputeIdentity(entry.Plan), StringComparison.Ordinal))
            throw new InvalidDataException("Deployment checkpoint does not match its plan identity.");
        return entry;
    }

    private sealed record Entry(string Identity, DeploymentPlan Plan, DeploymentCheckpoint Checkpoint);

    private sealed class Lease(FileStream fileLock, string path, string identity, DeploymentPlan plan, DeploymentCheckpoint? checkpoint)
        : IDeploymentLease
    {
        public DeploymentCheckpoint? Checkpoint { get; private set; } = checkpoint;

        public async Task SaveAsync(DeploymentCheckpoint value, CancellationToken cancellationToken)
        {
            var staging = path + ".tmp";
            try
            {
                await using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(output, new Entry(identity, plan, value), cancellationToken: cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                File.Move(staging, path, overwrite: true);
                Checkpoint = value;
            }
            finally { File.Delete(staging); }
        }

        public ValueTask DisposeAsync() => fileLock.DisposeAsync();
    }
}
