namespace RelaxKonOS.Server.Domain;

/// <summary>Encrypted secret material. The protected payload is not a user-visible DTO or normal backup field.</summary>
public sealed class TunnelSecret
{
    public Guid Id { get; set; }
    public Guid ServerProfileId { get; set; }
    public string Purpose { get; set; } = "token";
    public string ProtectedValue { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
