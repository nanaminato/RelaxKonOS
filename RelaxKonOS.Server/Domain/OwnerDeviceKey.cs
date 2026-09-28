namespace RelaxKonOS.Server.Domain;

/// <summary>
/// Public key for a Windows workstation owner device. Private key material never reaches the
/// server. Revocation is durable so a lost phone cannot regain access after a service restart.
/// </summary>
public sealed class OwnerDeviceKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = string.Empty;
    public string PublicKeySpki { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
