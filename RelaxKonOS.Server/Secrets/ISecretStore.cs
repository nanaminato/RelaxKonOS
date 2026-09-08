namespace RelaxKonOS.Server.Secrets;

/// <summary>Server-only secret abstraction. Callers receive plaintext only for an immediate protected operation.</summary>
public interface ISecretStore
{
    Task SetProfileTokenAsync(Guid profileId, string token, CancellationToken cancellationToken);
    Task<string?> GetProfileTokenAsync(Guid profileId, CancellationToken cancellationToken);
    Task<bool> HasProfileTokenAsync(Guid profileId, CancellationToken cancellationToken);
    Task DeleteProfileSecretsAsync(Guid profileId, CancellationToken cancellationToken);
}
