using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Domain;

/// <summary>Persistent FRP-independent tunnel desired state. Generated configuration is not stored here.</summary>
public sealed class TunnelDefinition
{
    public Guid Id { get; set; }
    public Guid ServerProfileId { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProviderId { get; set; } = "frp";
    public TunnelProtocol Protocol { get; set; }
    public string LocalHost { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; }
    public int? RemotePort { get; set; }
    public string? Domain { get; set; }
    public bool Enabled { get; set; }
    public bool Encryption { get; set; }
    public bool Compression { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
