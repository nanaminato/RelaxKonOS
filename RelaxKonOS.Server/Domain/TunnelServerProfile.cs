using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Domain;

/// <summary>Desired state only. Secret bytes are held separately and are never projected from this entity.</summary>
public sealed class TunnelServerProfile
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 7000;
    public TunnelAuthKind AuthKind { get; set; }
    public TunnelTlsMode TlsMode { get; set; }
    public TunnelRuntimeMode RuntimeMode { get; set; }
    public string? ExternalExecutablePath { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
