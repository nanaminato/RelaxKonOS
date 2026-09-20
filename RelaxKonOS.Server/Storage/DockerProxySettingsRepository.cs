using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage;

/// <summary>
/// Persistent host-global Docker proxy preference. Protection of the credential-bearing proxy
/// values is the storage layer's responsibility, never the caller's.
/// </summary>
public interface IDockerProxySettingsRepository
{
    Task<DockerProxySetting?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DockerProxySetting setting, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Development-only implementation with the same single-record semantics as SQLite.</summary>
public sealed class InMemoryDockerProxySettingsRepository : IDockerProxySettingsRepository
{
    private readonly object _gate = new();
    private DockerProxySetting? _setting;

    public Task<DockerProxySetting?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_setting is null ? null : Copy(_setting));
    }

    public Task SaveAsync(DockerProxySetting setting, CancellationToken cancellationToken = default)
    {
        lock (_gate) _setting = Copy(setting);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _setting = null;
        return Task.CompletedTask;
    }

    private static DockerProxySetting Copy(DockerProxySetting value) => new()
    {
        Id = DockerProxySetting.SingletonId,
        Enabled = value.Enabled,
        Source = value.Source,
        HttpProxy = value.HttpProxy,
        HttpsProxy = value.HttpsProxy,
        NoProxy = value.NoProxy,
        ApplyToEngine = value.ApplyToEngine,
        ApplyToBuild = value.ApplyToBuild,
        ApplyToImageTags = value.ApplyToImageTags,
        ApplyToRuntimeDownloads = value.ApplyToRuntimeDownloads,
        EngineApplied = value.EngineApplied,
        EngineProblemCode = value.EngineProblemCode,
        UpdatedAt = value.UpdatedAt,
        UpdatedBy = value.UpdatedBy,
    };
}
