namespace RelaxKonOS.Server.Domain;

public sealed class AliasCredential
{
    public Guid UserId { get; set; }
    public string? Alias { get; set; }
    public string? PasswordHash { get; set; }
    public bool SystemLoginEnabled { get; set; } = true;
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public AliasCredential Copy() => (AliasCredential)MemberwiseClone();
}
