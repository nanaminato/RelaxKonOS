using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Server.Domain;

/// <summary>A registry key is independent from its values so empty, user-created keys survive restart.</summary>
public sealed class RegistryKey
{
    public Guid UserId { get; set; }
    public RegistryScope Scope { get; set; }
    public Guid ScopeId { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
