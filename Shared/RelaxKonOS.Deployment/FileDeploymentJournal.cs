using System.Security.Cryptography;
using System.Text.Json;

namespace RelaxKonOS.Deployment;

/// <summary>Stores checkpoint files in the caller's private deployment state directory.</summary>
public sealed class FileDeploymentJournal(string directory) : IDeploymentJournal
{
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
            var identity = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                plan.Id,
                plan.Target,
                plan.ExpiresAt,
                Releases = plan.OrderedReleases.Select(release => new
                {
                    release.Package,
                    release.Version,
                    release.ArtifactIdentity,
                    release.Satisfied,
                    release.Requires,
                    Capabilities = release.Capabilities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                })
            })));
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

    private static async Task<Entry> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<Entry>(input, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Missing deployment checkpoint.");
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
