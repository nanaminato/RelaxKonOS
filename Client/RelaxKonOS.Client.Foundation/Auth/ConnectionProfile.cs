namespace RelaxKonOS.Client.Foundation.Auth;

/// <summary>Non-secret connection metadata. Platform hosts decide how, or whether, credentials are persisted.</summary>
public sealed record ConnectionProfile(string ServerUrl, string Identifier, DateTimeOffset LastUsedAt);

public interface IConnectionProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>Safe M0 default: a host must opt into durable storage explicitly.</summary>
public sealed class InMemoryConnectionProfileStore : IConnectionProfileStore
{
    private readonly List<ConnectionProfile> _profiles = [];

    public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.OrderByDescending(item => item.LastUsedAt).ToArray());

    public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        _profiles.RemoveAll(item => string.Equals(item.ServerUrl, profile.ServerUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Identifier, profile.Identifier, StringComparison.Ordinal));
        _profiles.Add(profile);
        return Task.CompletedTask;
    }
}
